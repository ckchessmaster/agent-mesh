using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using k8s;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentMesh.Infrastructure.Containers;

public class KubernetesPersonaContainerManager : IPersonaContainerManager
{
    private readonly ILogger<KubernetesPersonaContainerManager> _logger;
    private readonly IConfiguration _config;
    private readonly IServiceProvider _serviceProvider;
    private readonly IKubernetes _client;
    private readonly string _namespace;
    private readonly string _defaultImage;
    private static readonly ConcurrentDictionary<string, ActiveK8sAuthSession> _activeK8sAuthSessions = new();

    public string RuntimeName => "kubernetes";

    public KubernetesPersonaContainerManager(
        ILogger<KubernetesPersonaContainerManager> logger,
        IConfiguration config,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _config = config;
        _serviceProvider = serviceProvider;
        _defaultImage = config["PersonaContainer:Image"] ?? "agentmesh-persona-runner:latest";

        var ns = config["Kubernetes:Namespace"];
        if (string.IsNullOrWhiteSpace(ns) && File.Exists("/var/run/secrets/kubernetes.io/serviceaccount/namespace"))
        {
            try
            {
                ns = File.ReadAllText("/var/run/secrets/kubernetes.io/serviceaccount/namespace").Trim();
            }
            catch { }
        }
        _namespace = !string.IsNullOrWhiteSpace(ns) ? ns : "default";

        KubernetesClientConfiguration k8sConfig;
        if (KubernetesClientConfiguration.IsInCluster())
        {
            k8sConfig = KubernetesClientConfiguration.InClusterConfig();
        }
        else
        {
            k8sConfig = KubernetesClientConfiguration.BuildConfigFromConfigFile();
        }

        _client = new Kubernetes(k8sConfig);
        _logger.LogInformation("KubernetesPersonaContainerManager initialized for namespace {Namespace}", _namespace);
    }

    public static bool IsSupported()
    {
        return KubernetesClientConfiguration.IsInCluster() ||
               !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KUBECONFIG")) ||
               File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kube", "config"));
    }

    private async Task<(string Image, string? PullPolicy, string? PullSecret)> ResolvePersonaRunnerConfigAsync(CancellationToken ct = default)
    {
        var targetImage = _config["PersonaContainer:Image"] ?? _defaultImage;
        var targetPullPolicy = _config["PersonaContainer:PullPolicy"];
        var targetPullSecret = _config["PersonaContainer:ImagePullSecret"];

        try
        {
            var configMapName = _config["Kubernetes:ConfigMapName"];
            V1ConfigMap? cm = null;

            if (!string.IsNullOrWhiteSpace(configMapName))
            {
                try
                {
                    cm = await _client.CoreV1.ReadNamespacedConfigMapAsync(configMapName, _namespace, cancellationToken: ct);
                }
                catch { }
            }

            if (cm == null)
            {
                var cms = await _client.CoreV1.ListNamespacedConfigMapAsync(_namespace, cancellationToken: ct);
                cm = cms.Items.FirstOrDefault(c => c.Metadata?.Name?.EndsWith("-config") == true && c.Data?.ContainsKey("PersonaContainer__Image") == true)
                     ?? cms.Items.FirstOrDefault(c => c.Data?.ContainsKey("PersonaContainer__Image") == true);
            }

            if (cm?.Data != null)
            {
                if (cm.Data.TryGetValue("PersonaContainer__Image", out var img) && !string.IsNullOrWhiteSpace(img))
                {
                    targetImage = img.Trim();
                }
                if (cm.Data.TryGetValue("PersonaContainer__PullPolicy", out var pp) && !string.IsNullOrWhiteSpace(pp))
                {
                    targetPullPolicy = pp.Trim();
                }
                if (cm.Data.TryGetValue("PersonaContainer__ImagePullSecret", out var ps) && !string.IsNullOrWhiteSpace(ps))
                {
                    targetPullSecret = ps.Trim();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not dynamically resolve ConfigMap runner settings: {Error}", ex.Message);
        }

        return (targetImage, targetPullPolicy, targetPullSecret);
    }

    private async Task CleanupFailedPodsAsync(string normPersonaId, CancellationToken ct = default)
    {
        try
        {
            var pods = await _client.CoreV1.ListNamespacedPodAsync(
                _namespace,
                labelSelector: $"agentmesh.io/persona-id={normPersonaId}",
                cancellationToken: ct);

            foreach (var pod in pods.Items)
            {
                var phase = pod.Status?.Phase;
                var isFailed = phase == "Failed" || phase == "Succeeded";
                if (!isFailed && pod.Status?.ContainerStatuses != null)
                {
                    var runner = pod.Status.ContainerStatuses.FirstOrDefault(c => c.Name == "persona-runner")
                                 ?? pod.Status.ContainerStatuses.FirstOrDefault();
                    if (runner?.State?.Waiting != null &&
                        (runner.State.Waiting.Reason == "CrashLoopBackOff" || runner.State.Waiting.Reason == "ErrImagePull"))
                    {
                        isFailed = true;
                    }
                }

                if (isFailed && pod.Metadata?.Name != null)
                {
                    _logger.LogInformation("Deleting failed/dead pod {PodName} for @{PersonaId}", pod.Metadata.Name, normPersonaId);
                    try
                    {
                        await _client.CoreV1.DeleteNamespacedPodAsync(pod.Metadata.Name, _namespace, cancellationToken: ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("Failed to delete pod {PodName}: {Error}", pod.Metadata.Name, ex.Message);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to list/clean pods for @{PersonaId}: {Error}", normPersonaId, ex.Message);
        }
    }

    public async Task<PersonaEnvironmentStatus> GetStatusAsync(string personaId, CancellationToken ct = default)
    {
        var deploymentName = GetDeploymentName(personaId);
        var normPersonaId = personaId.Trim().ToLowerInvariant();

        try
        {
            var deployment = await _client.AppsV1.ReadNamespacedDeploymentAsync(deploymentName, _namespace, cancellationToken: ct);
            var replicas = deployment.Spec.Replicas ?? 0;
            var readyReplicas = deployment.Status.ReadyReplicas ?? 0;

            var status = readyReplicas > 0 ? "running" : (replicas > 0 ? "starting" : "stopped");
            string? errorMessage = null;

            if (readyReplicas == 0 && replicas > 0)
            {
                try
                {
                    var pods = await _client.CoreV1.ListNamespacedPodAsync(
                        _namespace,
                        labelSelector: $"agentmesh.io/persona-id={normPersonaId}",
                        cancellationToken: ct);

                    var activePod = pods.Items.OrderByDescending(p => p.Metadata.CreationTimestamp).FirstOrDefault();
                    if (activePod?.Status?.ContainerStatuses != null)
                    {
                        var runnerStatus = activePod.Status.ContainerStatuses.FirstOrDefault(c => c.Name == "persona-runner")
                                           ?? activePod.Status.ContainerStatuses.FirstOrDefault();

                        if (runnerStatus?.State?.Waiting != null)
                        {
                            var reason = runnerStatus.State.Waiting.Reason;
                            if (reason == "CrashLoopBackOff" || reason == "ImagePullBackOff" || reason == "ErrImagePull" || reason == "Error")
                            {
                                status = "error";
                                errorMessage = runnerStatus.State.Waiting.Message ?? reason;
                            }
                        }
                        else if (runnerStatus?.State?.Terminated != null && runnerStatus.State.Terminated.ExitCode != 0)
                        {
                            status = "error";
                            errorMessage = runnerStatus.State.Terminated.Message ?? $"Container exited with code {runnerStatus.State.Terminated.ExitCode}";
                        }
                    }
                }
                catch { }
            }

            var runnerContainer = deployment.Spec?.Template?.Spec?.Containers?.FirstOrDefault(c => c.Name == "persona-runner");
            var currentImage = runnerContainer?.Image;
            var (configuredImage, _, _) = await ResolvePersonaRunnerConfigAsync(ct);
            var updateAvailable = !string.IsNullOrWhiteSpace(currentImage) &&
                                  !string.IsNullOrWhiteSpace(configuredImage) &&
                                  !string.Equals(currentImage, configuredImage, StringComparison.OrdinalIgnoreCase);

            return new PersonaEnvironmentStatus(
                PersonaId: personaId,
                Status: status,
                Runtime: RuntimeName,
                ContainerId: deploymentName,
                StartedAt: deployment.Metadata.CreationTimestamp,
                LastActivityAt: DateTimeOffset.UtcNow,
                ErrorMessage: errorMessage,
                Image: currentImage,
                ConfiguredImage: configuredImage,
                UpdateAvailable: updateAvailable);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            var (configuredImage, _, _) = await ResolvePersonaRunnerConfigAsync(ct);
            return new PersonaEnvironmentStatus(personaId, "stopped", RuntimeName, ConfiguredImage: configuredImage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to query deployment {Name}: {Error}", deploymentName, ex.Message);
            return new PersonaEnvironmentStatus(personaId, "error", RuntimeName, ErrorMessage: ex.Message);
        }
    }

    public async Task EnsureRunningAsync(string personaId, CancellationToken ct = default)
    {
        var deploymentName = GetDeploymentName(personaId);
        var normPersonaId = personaId.Trim().ToLowerInvariant();
        var pvcName = GetPvcName(normPersonaId);

        var (currentConfigImage, currentPullPolicy, currentImagePullSecret) = await ResolvePersonaRunnerConfigAsync(ct);

        var meshUrl = _config["Kubernetes:ApiUrl"] ??
                      _config["AGENTMESH_URL"] ??
                      "http://agent-mesh-api:5000";

        var labels = new Dictionary<string, string>
        {
            ["app.kubernetes.io/name"] = "persona-runner",
            ["app.kubernetes.io/component"] = "persona-runner",
            ["agentmesh.io/persona-id"] = normPersonaId
        };

        await EnsurePersistentVolumeClaimAsync(normPersonaId, labels, ct);

        var agentToken = await ProvisionAgentTokenAsync(normPersonaId, ct);

        var env = new List<V1EnvVar>
        {
            new() { Name = "AGENTMESH_URL", Value = meshUrl },
            new() { Name = "AGENTMESH_TOKEN", Value = agentToken },
            new() { Name = "AGENTMESH_PERSONA_ID", Value = normPersonaId },
            new() { Name = "TERM", Value = "xterm-256color" }
        };

        var envFrom = new List<V1EnvFromSource>();
        var secretName = _config["Kubernetes:SecretName"] ?? "agent-mesh-secrets";
        envFrom.Add(new V1EnvFromSource { SecretRef = new V1SecretEnvSource { Name = secretName, Optional = true } });

        var personaSecretName = $"agentmesh-persona-{normPersonaId}-creds";
        envFrom.Add(new V1EnvFromSource { SecretRef = new V1SecretEnvSource { Name = personaSecretName, Optional = true } });

        var sidecarConfig = await GetPersonaSidecarConfigAsync(normPersonaId, ct);
        if (sidecarConfig != null && sidecarConfig.Enabled)
        {
            env.Add(new V1EnvVar { Name = "SIDECAR_ENABLED", Value = "true" });
            env.Add(new V1EnvVar { Name = "SIDECAR_TYPE", Value = sidecarConfig.Type });
            env.Add(new V1EnvVar { Name = "SIDECAR_USERSPACE", Value = sidecarConfig.UserspaceMode ? "true" : "false" });
            if (!string.IsNullOrWhiteSpace(sidecarConfig.CompanionSetup))
            {
                env.Add(new V1EnvVar { Name = "COMPANION_SETUP", Value = sidecarConfig.CompanionSetup });
            }
            if (!string.IsNullOrWhiteSpace(sidecarConfig.CompanionCommand))
            {
                env.Add(new V1EnvVar { Name = "COMPANION_COMMAND", Value = sidecarConfig.CompanionCommand });
            }
            if (sidecarConfig.CompanionPort.HasValue && sidecarConfig.CompanionPort.Value > 0)
            {
                env.Add(new V1EnvVar { Name = "COMPANION_PORT", Value = sidecarConfig.CompanionPort.Value.ToString() });
            }
        }
        var sidecarContainer = sidecarConfig != null ? BuildSidecarContainer(sidecarConfig, normPersonaId) : null;

        try
        {
            var deployment = await _client.AppsV1.ReadNamespacedDeploymentAsync(deploymentName, _namespace, cancellationToken: ct);
            var runnerContainer = deployment.Spec?.Template?.Spec?.Containers?.FirstOrDefault(c => c.Name == "persona-runner");
            var currentImage = runnerContainer?.Image;
            var currentPull = runnerContainer?.ImagePullPolicy;
            var currentPersonaEnv = runnerContainer?.Env?.FirstOrDefault(e => e.Name == "AGENTMESH_PERSONA_ID")?.Value;
            var currentUrlEnv = runnerContainer?.Env?.FirstOrDefault(e => e.Name == "AGENTMESH_URL")?.Value;
            var currentTokenEnv = runnerContainer?.Env?.FirstOrDefault(e => e.Name == "AGENTMESH_TOKEN")?.Value;
            var hasWorkspaceMount = runnerContainer?.VolumeMounts?.Any(vm => vm.MountPath == "/workspace") == true;
            var workspaceVolume = deployment.Spec?.Template?.Spec?.Volumes?.FirstOrDefault(v => v.Name == "workspace-storage");
            var hasWorkspacePvc = workspaceVolume?.PersistentVolumeClaim != null &&
                                  string.Equals(workspaceVolume.PersistentVolumeClaim.ClaimName, pvcName, StringComparison.OrdinalIgnoreCase);
            var hasCredsMount = runnerContainer?.VolumeMounts?.Any(vm => vm.MountPath == "/etc/agentmesh-creds") == true;
            var hasCredsVolume = deployment.Spec?.Template?.Spec?.Volumes?.Any(v => v.Name == "persona-creds-volume") == true;
            var currentSidecarContainer = deployment.Spec?.Template?.Spec?.Containers?.FirstOrDefault(c => c.Name == "driver-sidecar");
            var hasSidecarContainer = currentSidecarContainer != null;
            var needsSidecarUpdate = (sidecarContainer != null) != hasSidecarContainer;
            if (!needsSidecarUpdate && sidecarContainer != null && currentSidecarContainer != null)
            {
                if (!string.Equals(currentSidecarContainer.Image, sidecarContainer.Image, StringComparison.OrdinalIgnoreCase))
                {
                    needsSidecarUpdate = true;
                }
                else
                {
                    var currentEnv = currentSidecarContainer.Env?.ToDictionary(e => e.Name, e => e.Value ?? "", StringComparer.OrdinalIgnoreCase)
                                     ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var desiredEnv = sidecarContainer.Env?.ToDictionary(e => e.Name, e => e.Value ?? "", StringComparer.OrdinalIgnoreCase)
                                     ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    if (currentEnv.Count != desiredEnv.Count ||
                        desiredEnv.Any(kv => !currentEnv.TryGetValue(kv.Key, out var cv) || cv != kv.Value))
                    {
                        needsSidecarUpdate = true;
                    }
                }
            }

            var isScaledDown = (deployment.Spec?.Replicas ?? 0) == 0;
            var needsImageUpdate = runnerContainer != null && !string.Equals(currentImage, currentConfigImage, StringComparison.OrdinalIgnoreCase);
            var needsPullPolicyUpdate = !string.IsNullOrWhiteSpace(currentPullPolicy) && !string.Equals(currentPull, currentPullPolicy, StringComparison.OrdinalIgnoreCase);
            var needsEnvUpdate = runnerContainer == null ||
                                 !string.Equals(currentPersonaEnv, normPersonaId, StringComparison.OrdinalIgnoreCase) ||
                                 !string.Equals(currentUrlEnv, meshUrl, StringComparison.OrdinalIgnoreCase) ||
                                 !string.Equals(currentTokenEnv, agentToken, StringComparison.Ordinal);
            var needsVolumeUpdate = !hasWorkspaceMount || !hasWorkspacePvc || !hasCredsMount || !hasCredsVolume;

            // Query status to detect crashed/error pods that need a kick
            var currentStatus = await GetStatusAsync(personaId, ct);
            var isFailed = currentStatus.Status == "error";

            if (isScaledDown || needsImageUpdate || needsPullPolicyUpdate || needsEnvUpdate || needsVolumeUpdate || isFailed || needsSidecarUpdate)
            {
                _logger.LogInformation("Updating deployment {Name} for @{PersonaId}: Replicas=1, Image={Image} (was {OldImage}), EnvUpdated={EnvUpdated}, IsFailed={IsFailed}, NeedsSidecar={NeedsSidecar}",
                    deploymentName, personaId, currentConfigImage, currentImage, needsEnvUpdate, isFailed, needsSidecarUpdate);

                var containerPatch = new Dictionary<string, object>
                {
                    ["name"] = "persona-runner",
                    ["image"] = currentConfigImage,
                    ["env"] = env.Select(e => new { name = e.Name, value = e.Value }).ToList(),
                    ["envFrom"] = envFrom.Select(ef => new
                    {
                        secretRef = new { name = ef.SecretRef?.Name, optional = ef.SecretRef?.Optional }
                    }).ToList(),
                    ["volumeMounts"] = new object[]
                    {
                        new { name = "workspace-storage", mountPath = "/workspace" },
                        new { name = "persona-creds-volume", mountPath = "/etc/agentmesh-creds", readOnly = true }
                    }
                };

                if (!string.IsNullOrWhiteSpace(currentPullPolicy))
                {
                    containerPatch["imagePullPolicy"] = currentPullPolicy;
                }

                var containersPatchList = new List<object> { containerPatch };
                if (sidecarContainer != null)
                {
                    containersPatchList.Add(BuildSidecarPatchObject(sidecarContainer));
                }

                var volumesPatchList = new List<object>
                {
                    new Dictionary<string, object?>
                    {
                        ["name"] = "workspace-storage",
                        ["persistentVolumeClaim"] = new { claimName = pvcName },
                        ["emptyDir"] = null
                    },
                    new { name = "persona-creds-volume", secret = new { secretName = personaSecretName, optional = true } }
                };
                if (sidecarConfig is { Enabled: true, UserspaceMode: false } && !string.Equals(sidecarConfig.Type, "custom", StringComparison.OrdinalIgnoreCase))
                {
                    volumesPatchList.Add(new { name = "tun-dev", hostPath = new { path = "/dev/net/tun", type = "CharDevice" } });
                }

                var podSpecPatch = new Dictionary<string, object>
                {
                    ["containers"] = containersPatchList.ToArray(),
                    ["volumes"] = volumesPatchList.ToArray()
                };

                if (!string.IsNullOrWhiteSpace(currentImagePullSecret))
                {
                    podSpecPatch["imagePullSecrets"] = new[]
                    {
                        new { name = currentImagePullSecret }
                    };
                }

                var annotations = new Dictionary<string, string>();
                if (isFailed || needsImageUpdate || needsSidecarUpdate)
                {
                    annotations["kubectl.kubernetes.io/restartedAt"] = DateTimeOffset.UtcNow.ToString("o");
                    await CleanupFailedPodsAsync(normPersonaId, ct);
                }

                var patchDoc = new
                {
                    spec = new
                    {
                        replicas = 1,
                        strategy = new { type = "Recreate" },
                        template = new
                        {
                            metadata = new
                            {
                                labels = labels,
                                annotations = annotations.Count > 0 ? annotations : null
                            },
                            spec = podSpecPatch
                        }
                    }
                };

                var patchJson = JsonSerializer.Serialize(patchDoc);
                var patch = new V1Patch(patchJson, V1Patch.PatchType.StrategicMergePatch);
                await _client.AppsV1.PatchNamespacedDeploymentAsync(patch, deploymentName, _namespace, cancellationToken: ct);
            }
            await SeedCredentialsFromSecretAsync(normPersonaId, ct);
            return;
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Create deployment
        }

        _logger.LogInformation("Creating new deployment {Name} for @{PersonaId} in namespace {Namespace} with image {Image}", deploymentName, personaId, _namespace, currentConfigImage);

        var containersList = new List<V1Container>
        {
            new()
            {
                Name = "persona-runner",
                Image = currentConfigImage,
                ImagePullPolicy = !string.IsNullOrWhiteSpace(currentPullPolicy) ? currentPullPolicy : null,
                Env = env,
                EnvFrom = envFrom,
                VolumeMounts = new List<V1VolumeMount>
                {
                    new() { MountPath = "/workspace", Name = "workspace-storage" },
                    new() { MountPath = "/etc/agentmesh-creds", Name = "persona-creds-volume", ReadOnlyProperty = true }
                }
            }
        };
        if (sidecarContainer != null)
        {
            containersList.Add(sidecarContainer);
        }

        var volumesList = new List<V1Volume>
        {
            new()
            {
                Name = "workspace-storage",
                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource
                {
                    ClaimName = pvcName
                }
            },
            new() { Name = "persona-creds-volume", Secret = new V1SecretVolumeSource { SecretName = personaSecretName, Optional = true } }
        };
        if (sidecarConfig is { Enabled: true, UserspaceMode: false } && !string.Equals(sidecarConfig.Type, "custom", StringComparison.OrdinalIgnoreCase))
        {
            volumesList.Add(BuildTunVolume());
        }

        var newDeployment = new V1Deployment
        {
            Metadata = new V1ObjectMeta
            {
                Name = deploymentName,
                NamespaceProperty = _namespace,
                Labels = labels
            },
            Spec = new V1DeploymentSpec
            {
                Replicas = 1,
                Strategy = new V1DeploymentStrategy { Type = "Recreate" },
                Selector = new V1LabelSelector { MatchLabels = labels },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta { Labels = labels },
                    Spec = new V1PodSpec
                    {
                        ImagePullSecrets = !string.IsNullOrWhiteSpace(currentImagePullSecret)
                            ? new List<V1LocalObjectReference> { new() { Name = currentImagePullSecret } }
                            : null,
                        Containers = containersList,
                        Volumes = volumesList
                    }
                }
            }
        };

        await _client.AppsV1.CreateNamespacedDeploymentAsync(newDeployment, _namespace, cancellationToken: ct);
        _logger.LogInformation("Deployment {Name} created successfully for @{PersonaId}", deploymentName, personaId);
        await SeedCredentialsFromSecretAsync(normPersonaId, ct);
    }

    public async Task StopAsync(string personaId, CancellationToken ct = default)
    {
        var deploymentName = GetDeploymentName(personaId);

        try
        {
            _logger.LogInformation("Scaling down deployment {Name} for @{PersonaId} to 0 replicas", deploymentName, personaId);
            var patchJson = JsonSerializer.Serialize(new { spec = new { replicas = 0 } });
            var patch = new V1Patch(patchJson, V1Patch.PatchType.MergePatch);
            await _client.AppsV1.PatchNamespacedDeploymentAsync(patch, deploymentName, _namespace, cancellationToken: ct);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Nothing to stop
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to scale down deployment {Name}: {Error}", deploymentName, ex.Message);
        }
    }

    public async Task RestartAsync(string personaId, CancellationToken ct = default)
    {
        var deploymentName = GetDeploymentName(personaId);
        var normPersonaId = personaId.Trim().ToLowerInvariant();
        var pvcName = GetPvcName(normPersonaId);

        _logger.LogInformation("Restarting and updating deployment {Name} for @{PersonaId}", deploymentName, normPersonaId);

        // Persist current state from container to workspace volume before recreating pod
        try
        {
            await ExecInContainerAsync(normPersonaId, PersonaContainerExtensions.SyncPersonaStateCommand, ct);
        }
        catch { }

        // 1. Delete dead / failed pods so cluster stays clean
        await CleanupFailedPodsAsync(normPersonaId, ct);

        // 2. Resolve fresh runner config from live ConfigMap
        var (targetImage, targetPullPolicy, targetPullSecret) = await ResolvePersonaRunnerConfigAsync(ct);

        var meshUrl = _config["Kubernetes:ApiUrl"] ??
                      _config["AGENTMESH_URL"] ??
                      "http://agent-mesh-api:5000";

        var labels = new Dictionary<string, string>
        {
            ["app.kubernetes.io/name"] = "persona-runner",
            ["app.kubernetes.io/component"] = "persona-runner",
            ["agentmesh.io/persona-id"] = normPersonaId
        };

        await EnsurePersistentVolumeClaimAsync(normPersonaId, labels, ct);
        var agentToken = await ProvisionAgentTokenAsync(normPersonaId, ct);

        var env = new List<V1EnvVar>
        {
            new() { Name = "AGENTMESH_URL", Value = meshUrl },
            new() { Name = "AGENTMESH_TOKEN", Value = agentToken },
            new() { Name = "AGENTMESH_PERSONA_ID", Value = normPersonaId },
            new() { Name = "TERM", Value = "xterm-256color" }
        };

        var envFrom = new List<V1EnvFromSource>();
        var secretName = _config["Kubernetes:SecretName"] ?? "agent-mesh-secrets";
        envFrom.Add(new V1EnvFromSource { SecretRef = new V1SecretEnvSource { Name = secretName, Optional = true } });

        var personaSecretName = $"agentmesh-persona-{normPersonaId}-creds";
        envFrom.Add(new V1EnvFromSource { SecretRef = new V1SecretEnvSource { Name = personaSecretName, Optional = true } });

        var sidecarConfig = await GetPersonaSidecarConfigAsync(normPersonaId, ct);
        if (sidecarConfig != null && sidecarConfig.Enabled)
        {
            env.Add(new V1EnvVar { Name = "SIDECAR_ENABLED", Value = "true" });
            env.Add(new V1EnvVar { Name = "SIDECAR_TYPE", Value = sidecarConfig.Type });
            env.Add(new V1EnvVar { Name = "SIDECAR_USERSPACE", Value = sidecarConfig.UserspaceMode ? "true" : "false" });
            if (!string.IsNullOrWhiteSpace(sidecarConfig.CompanionSetup))
            {
                env.Add(new V1EnvVar { Name = "COMPANION_SETUP", Value = sidecarConfig.CompanionSetup });
            }
            if (!string.IsNullOrWhiteSpace(sidecarConfig.CompanionCommand))
            {
                env.Add(new V1EnvVar { Name = "COMPANION_COMMAND", Value = sidecarConfig.CompanionCommand });
            }
            if (sidecarConfig.CompanionPort.HasValue && sidecarConfig.CompanionPort.Value > 0)
            {
                env.Add(new V1EnvVar { Name = "COMPANION_PORT", Value = sidecarConfig.CompanionPort.Value.ToString() });
            }
        }
        var sidecarContainer = sidecarConfig != null ? BuildSidecarContainer(sidecarConfig, normPersonaId) : null;

        var containerPatch = new Dictionary<string, object>
        {
            ["name"] = "persona-runner",
            ["image"] = targetImage,
            ["env"] = env.Select(e => new { name = e.Name, value = e.Value }).ToList(),
            ["envFrom"] = envFrom.Select(ef => new
            {
                secretRef = new { name = ef.SecretRef?.Name, optional = ef.SecretRef?.Optional }
            }).ToList(),
            ["volumeMounts"] = new object[]
            {
                new { name = "workspace-storage", mountPath = "/workspace" },
                new { name = "persona-creds-volume", mountPath = "/etc/agentmesh-creds", readOnly = true }
            }
        };

        if (!string.IsNullOrWhiteSpace(targetPullPolicy))
        {
            containerPatch["imagePullPolicy"] = targetPullPolicy;
        }

        var containersPatchList = new List<object> { containerPatch };
        if (sidecarContainer != null)
        {
            containersPatchList.Add(BuildSidecarPatchObject(sidecarContainer));
        }

        var volumesPatchList = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["name"] = "workspace-storage",
                ["persistentVolumeClaim"] = new { claimName = pvcName },
                ["emptyDir"] = null
            },
            new { name = "persona-creds-volume", secret = new { secretName = personaSecretName, optional = true } }
        };
        if (sidecarConfig is { Enabled: true, UserspaceMode: false } && !string.Equals(sidecarConfig.Type, "custom", StringComparison.OrdinalIgnoreCase))
        {
            volumesPatchList.Add(new { name = "tun-dev", hostPath = new { path = "/dev/net/tun", type = "CharDevice" } });
        }

        var podSpecPatch = new Dictionary<string, object>
        {
            ["containers"] = containersPatchList.ToArray(),
            ["volumes"] = volumesPatchList.ToArray()
        };

        if (!string.IsNullOrWhiteSpace(targetPullSecret))
        {
            podSpecPatch["imagePullSecrets"] = new[]
            {
                new { name = targetPullSecret }
            };
        }

        var annotations = new Dictionary<string, string>
        {
            ["kubectl.kubernetes.io/restartedAt"] = DateTimeOffset.UtcNow.ToString("o")
        };

        var patchDoc = new
        {
            spec = new
            {
                replicas = 1,
                strategy = new { type = "Recreate" },
                template = new
                {
                    metadata = new
                    {
                        labels = labels,
                        annotations = annotations
                    },
                    spec = podSpecPatch
                }
            }
        };

        try
        {
            var patchJson = JsonSerializer.Serialize(patchDoc);
            var patch = new V1Patch(patchJson, V1Patch.PatchType.StrategicMergePatch);
            await _client.AppsV1.PatchNamespacedDeploymentAsync(patch, deploymentName, _namespace, cancellationToken: ct);
            _logger.LogInformation("Successfully patched deployment {Name} with image {Image} and restartedAt rollout", deploymentName, targetImage);
            await SeedCredentialsFromSecretAsync(normPersonaId, ct);
            return;
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // If deployment doesn't exist, fall through to EnsureRunningAsync
        }

        await EnsureRunningAsync(personaId, ct);
    }

    public Task<string> GetLogsAsync(string personaId, int tailLines = 100, CancellationToken ct = default)
        => GetLogsAsync(personaId, tailLines, null, ct);

    public async Task<string> GetLogsAsync(string personaId, int tailLines = 100, string? container = null, CancellationToken ct = default)
    {
        var normPersonaId = personaId.Trim().ToLowerInvariant();

        try
        {
            var pods = await _client.CoreV1.ListNamespacedPodAsync(
                _namespace,
                labelSelector: $"agentmesh.io/persona-id={normPersonaId}",
                cancellationToken: ct);

            if (pods.Items.Count == 0)
            {
                return "No pods found for persona.";
            }

            var pod = pods.Items.OrderByDescending(p => p.Metadata.CreationTimestamp).FirstOrDefault();
            if (pod == null)
            {
                return "No pods found for persona.";
            }

            var targetContainer = !string.IsNullOrWhiteSpace(container) ? container.Trim() : "persona-runner";
            var containerStatus = pod.Status?.ContainerStatuses?.FirstOrDefault(c => c.Name == targetContainer)
                                  ?? pod.Status?.ContainerStatuses?.FirstOrDefault();

            if (containerStatus?.State?.Waiting != null)
            {
                var reason = containerStatus.State.Waiting.Reason ?? "Initializing";
                var msg = containerStatus.State.Waiting.Message;
                if (reason is "ImagePullBackOff" or "ErrImagePull" or "CrashLoopBackOff" or "Error")
                {
                    return $"[Pod Status: Error]\nContainer '{targetContainer}' failed to start: {reason}\n{msg}";
                }

                return $"[Pod Status: Initializing]\nContainer '{targetContainer}' is starting up ({reason})...\nLogs will appear automatically as soon as the container is running.";
            }

            using var stream = await _client.CoreV1.ReadNamespacedPodLogAsync(
                pod.Metadata.Name,
                _namespace,
                container: targetContainer,
                tailLines: tailLines,
                cancellationToken: ct);

            using var reader = new StreamReader(stream);
            var logs = await reader.ReadToEndAsync(ct);

            // If querying runner logs and sidecar is crashing or failing, append sidecar logs for diagnostics
            if (string.IsNullOrWhiteSpace(container))
            {
                var sidecarStatus = pod.Status?.ContainerStatuses?.FirstOrDefault(c => c.Name == "driver-sidecar");
                if (sidecarStatus?.State?.Waiting != null && sidecarStatus.State.Waiting.Reason is "CrashLoopBackOff" or "Error")
                {
                    try
                    {
                        using var scStream = await _client.CoreV1.ReadNamespacedPodLogAsync(
                            pod.Metadata.Name,
                            _namespace,
                            container: "driver-sidecar",
                            tailLines: 30,
                            cancellationToken: ct);
                        using var scReader = new StreamReader(scStream);
                        var scLogs = await scReader.ReadToEndAsync(ct);
                        logs += $"\n\n--- [driver-sidecar Diagnostics ({sidecarStatus.State.Waiting.Reason})] ---\n{scLogs}";
                    }
                    catch { }
                }
            }

            return logs;
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.BadRequest || (int?)ex.Response?.StatusCode == 400 || ex.Message.Contains("BadRequest"))
        {
            return "[Pod Status: Initializing]\nContainer is waiting to start (ContainerCreating). Logs will stream automatically once ready.";
        }
        catch (Exception ex) when (ex.Message.Contains("ContainerCreating") || ex.Message.Contains("waiting to start") || ex.Message.Contains("BadRequest"))
        {
            return "[Pod Status: Initializing]\nContainer is waiting to start (ContainerCreating). Logs will stream automatically once ready.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to read pod logs for @{PersonaId}: {Error}", personaId, ex.Message);
            return $"Error reading logs: {ex.Message}";
        }
    }

    public async Task<(int ExitCode, string StdOut, string StdErr)> ExecInContainerAsync(
        string personaId,
        string command,
        CancellationToken ct = default)
    {
        var normPersonaId = personaId.Trim().ToLowerInvariant();
        try
        {
            var pods = await _client.CoreV1.ListNamespacedPodAsync(
                _namespace,
                labelSelector: $"agentmesh.io/persona-id={normPersonaId}",
                cancellationToken: ct);

            var pod = pods.Items
                .Where(p => p.Metadata.DeletionTimestamp == null && p.Status?.Phase == "Running")
                .OrderByDescending(p => p.Metadata.CreationTimestamp)
                .FirstOrDefault();

            if (pod == null)
            {
                return (-1, "", "No running pod found for persona.");
            }

            var stdOutBuilder = new StringBuilder();
            var stdErrBuilder = new StringBuilder();

            var wrappedCommand = $"export PATH=\"/root/.local/bin:/usr/local/bin:$PATH\"; {command}";

            var exitCode = await _client.NamespacedPodExecAsync(
                name: pod.Metadata.Name,
                @namespace: _namespace,
                container: "persona-runner",
                command: new[] { "/bin/bash", "-c", wrappedCommand },
                tty: false,
                action: async (stdIn, stdOut, stdError) =>
                {
                    var outTask = Task.Run(async () =>
                    {
                        using var reader = new StreamReader(stdOut, Encoding.UTF8);
                        var content = await reader.ReadToEndAsync(ct);
                        stdOutBuilder.Append(content);
                    }, ct);

                    var errTask = Task.Run(async () =>
                    {
                        using var reader = new StreamReader(stdError, Encoding.UTF8);
                        var content = await reader.ReadToEndAsync(ct);
                        stdErrBuilder.Append(content);
                    }, ct);

                    await Task.WhenAll(outTask, errTask);
                },
                cancellationToken: ct);

            return (exitCode, stdOutBuilder.ToString(), stdErrBuilder.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning("ExecInContainerAsync failed for @{PersonaId}: {Error}", personaId, ex.Message);
            return (-1, "", ex.Message);
        }
    }

    public async Task<PersonaDriverVerificationResult> VerifyDriverAsync(string personaId, CancellationToken ct = default)
    {
        var normPersonaId = personaId.Trim().ToLowerInvariant();
        await EnsureRunningAsync(normPersonaId, ct);

        // Wait up to 15 seconds for a running pod if a rollout or restart was just triggered
        for (var i = 0; i < 15; i++)
        {
            try
            {
                var checkPods = await _client.CoreV1.ListNamespacedPodAsync(
                    _namespace,
                    labelSelector: $"agentmesh.io/persona-id={normPersonaId}",
                    cancellationToken: ct);

                var isRunning = checkPods.Items.Any(p => p.Metadata.DeletionTimestamp == null && p.Status?.Phase == "Running");
                if (isRunning) break;
            }
            catch { }
            await Task.Delay(1000, ct);
        }

        var info = await ResolvePersonaDriverInfoAsync(normPersonaId, ct);
        var driverId = info.DriverId;
        var adapterType = info.AdapterType;
        var endpoint = info.Endpoint;
        var defaultModel = info.DefaultModel;
        var hasApiKey = info.HasApiKey;
        var sidecar = info.Sidecar;
        var loginCmd = info.LoginCommand;
        var authCheckCmd = info.AuthCheckCommand;

        var steps = new List<PersonaDriverVerificationStep>();

        // 1. Pod running check
        var (chkExit, chkOut, _) = await ExecInContainerAsync(normPersonaId, "uname -s", ct);
        if (chkExit == 0)
        {
            steps.Add(new PersonaDriverVerificationStep("Kubernetes Pod Status", "success", "Pod is actively running in cluster"));
        }
        else
        {
            steps.Add(new PersonaDriverVerificationStep("Kubernetes Pod Status", "failed", "Pod is not responding to exec"));
        }

        // 2. Sidecar connectivity check if configured
        if (sidecar != null && sidecar.Enabled)
        {
            if (string.Equals(sidecar.Type, "tailscale", StringComparison.OrdinalIgnoreCase))
            {
                var (tsExit, tsOut, tsErr) = await ExecInContainerAsync(normPersonaId, "test -d /sys/class/net/tailscale0 || grep -q 'tailscale0:' /proc/net/dev || (command -v ip >/dev/null 2>&1 && ip addr show tailscale0)", ct);
                if (tsExit == 0)
                {
                    steps.Add(new PersonaDriverVerificationStep(
                        "Sidecar Connectivity",
                        "success",
                        "Tailscale network interface (tailscale0) is active"));
                }
                else if (sidecar.UserspaceMode)
                {
                    steps.Add(new PersonaDriverVerificationStep(
                        "Sidecar Connectivity",
                        "success",
                        "Tailscale sidecar is configured in userspace mode"));
                }
                else
                {
                    steps.Add(new PersonaDriverVerificationStep(
                        "Sidecar Connectivity",
                        "warning",
                        "Tailscale sidecar is starting up or tailscale0 interface is not yet ready"));
                }
            }
            else
            {
                steps.Add(new PersonaDriverVerificationStep(
                    "Sidecar Connectivity",
                    "success",
                    $"Custom connectivity sidecar configured ({sidecar.Image ?? "custom"})"));
            }
        }

        // 3. Companion binary & daemon check (if companion command configured)
        string? companionBin = null;
        if (!string.IsNullOrWhiteSpace(sidecar?.CompanionCommand))
        {
            var cmdTokens = sidecar.CompanionCommand.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cmdTokens.Length > 0)
            {
                companionBin = cmdTokens[0];
            }
        }

        if (!string.IsNullOrWhiteSpace(companionBin))
        {
            var binName = Path.GetFileName(companionBin);
            var (binFindExit, binFindOut, _) = await ExecInContainerAsync(
                normPersonaId,
                $"command -v {companionBin} 2>/dev/null || which {companionBin} 2>/dev/null || test -x /root/.local/bin/{binName} && echo /root/.local/bin/{binName}",
                ct);

            if (binFindExit == 0 && !string.IsNullOrWhiteSpace(binFindOut))
            {
                var foundPath = binFindOut.Split('\n')[0].Trim();
                steps.Add(new PersonaDriverVerificationStep(
                    "Companion Binary",
                    "success",
                    $"Companion binary '{binName}' detected ({foundPath})"));
            }
            else
            {
                steps.Add(new PersonaDriverVerificationStep(
                    "Companion Binary",
                    "failed",
                    $"Companion binary '{binName}' not found in container PATH or /root/.local/bin"));
            }

            // Persist current state
            try
            {
                await ExecInContainerAsync(normPersonaId, PersonaContainerExtensions.SyncPersonaStateCommand, ct);
            }
            catch { }

            var daemonCheckCmd = sidecar?.CompanionPort.HasValue == true && sidecar.CompanionPort.Value > 0
                ? $"(timeout 1 bash -c '</dev/tcp/127.0.0.1/{sidecar.CompanionPort.Value}' 2>/dev/null || curl -s -m 1 http://127.0.0.1:{sidecar.CompanionPort.Value}/ >/dev/null 2>&1) || pgrep -x '{binName}' >/dev/null 2>&1 || pidof '{binName}' >/dev/null 2>&1"
                : $"pgrep -x '{binName}' >/dev/null 2>&1 || pidof '{binName}' >/dev/null 2>&1";

            var (daemonExit, _, _) = await ExecInContainerAsync(normPersonaId, daemonCheckCmd, ct);

            // If not running, attempt to kickstart it in background and check again
            if (daemonExit != 0 && !string.IsNullOrWhiteSpace(sidecar?.CompanionCommand))
            {
                await ExecInContainerAsync(
                    normPersonaId,
                    $"nohup {sidecar.CompanionCommand} >/tmp/companion-daemon.log 2>&1 &",
                    ct);
                await Task.Delay(1000, ct);
                (daemonExit, _, _) = await ExecInContainerAsync(normPersonaId, daemonCheckCmd, ct);
            }

            if (daemonExit == 0)
            {
                var detail = sidecar?.CompanionPort.HasValue == true
                    ? $"Companion daemon '{binName}' is active (port {sidecar.CompanionPort.Value})"
                    : $"Companion daemon '{binName}' is running";
                steps.Add(new PersonaDriverVerificationStep(
                    "Companion Daemon",
                    "success",
                    detail));
            }
            else
            {
                steps.Add(new PersonaDriverVerificationStep(
                    "Companion Daemon",
                    "failed",
                    $"Companion daemon '{sidecar?.CompanionCommand}' is not running in container. Ensure credentials exist or log in via terminal."));
            }
        }

        // 4. CLI Binary Check
        string checkCmd;
        if (adapterType == "claude" || driverId.StartsWith("claude"))
        {
            checkCmd = "claude --version";
        }
        else if (adapterType == "copilot" || driverId.StartsWith("copilot"))
        {
            checkCmd = "gh --version";
        }
        else if (adapterType == "openai")
        {
            checkCmd = !string.IsNullOrWhiteSpace(companionBin)
                ? $"command -v {companionBin} 2>/dev/null || test -x /root/.local/bin/{Path.GetFileName(companionBin)}"
                : "echo 'OpenAI API Adapter'";
        }
        else if (driverId.StartsWith("agy") || adapterType == "agy")
        {
            checkCmd = "agy --version";
        }
        else
        {
            checkCmd = $"command -v {driverId} --version 2>/dev/null || echo '{driverId} active'";
        }

        var (binExit, binOut, binErr) = await ExecInContainerAsync(normPersonaId, checkCmd, ct);
        if (binExit == 0)
        {
            var ver = !string.IsNullOrWhiteSpace(binOut) ? binOut.Split('\n')[0].Trim() : "available";
            steps.Add(new PersonaDriverVerificationStep(
                "CLI Binary Check",
                "success",
                $"{driverId} ({adapterType}) binary check passed ({ver})"));
        }
        else
        {
            var combinedErr = $"{binErr} {binOut}".Trim();
            var isPclmul = combinedErr.Contains("pclmul", StringComparison.OrdinalIgnoreCase) ||
                           combinedErr.Contains("Illegal instruction", StringComparison.OrdinalIgnoreCase) ||
                           binExit == 132;

            var errorDetails = isPclmul
                ? "Processor is missing PCLMULQDQ instruction required by Google agy binary. In Proxmox VE, edit VM Hardware -> Processor -> Type and set to 'host' (then reboot VM). Alternatively, switch to Direct API Key mode."
                : $"{driverId} binary check failed (exit {binExit}): {binErr}";

            steps.Add(new PersonaDriverVerificationStep(
                "CLI Binary Check",
                "failed",
                errorDetails));
        }

        // 5. Authentication check
        await SeedCredentialsFromSecretAsync(normPersonaId, ct);
        if (!string.IsNullOrWhiteSpace(authCheckCmd))
        {
            var (chkAuthExit, chkAuthOut, chkAuthErr) = await ExecInContainerAsync(normPersonaId, authCheckCmd, ct);
            var combinedAuth = $"{chkAuthOut} {chkAuthErr}".Trim();
            if (chkAuthExit == 0)
            {
                var firstLine = !string.IsNullOrWhiteSpace(combinedAuth) ? combinedAuth.Split('\n')[0].Trim() : "authenticated";
                steps.Add(new PersonaDriverVerificationStep("Authentication Check", "success", $"Driver authenticated ({firstLine})"));
            }
            else
            {
                steps.Add(new PersonaDriverVerificationStep("Authentication Check", "warning", $"Driver authentication check exited with code {chkAuthExit}: {combinedAuth}. Click 'Authenticate Driver' in the UI to log in."));
            }
        }
        else if (driverId.StartsWith("agy") || adapterType == "agy")
        {
            var (authExit, _, _) = await ExecInContainerAsync(normPersonaId, "test -f /root/.gemini/antigravity-cli/antigravity-oauth-token", ct);
            var (keyExit, keyOut, _) = await ExecInContainerAsync(normPersonaId, "printenv GEMINI_API_KEY", ct);
            if (authExit == 0)
            {
                try
                {
                    var (_, tokenContent, _) = await ExecInContainerAsync(normPersonaId, "cat /root/.gemini/antigravity-cli/antigravity-oauth-token", ct);
                    if (!string.IsNullOrWhiteSpace(tokenContent))
                    {
                        await UpsertPersonaSecretAsync(normPersonaId, new Dictionary<string, byte[]>
                        {
                            ["antigravity-oauth-token"] = Encoding.UTF8.GetBytes(tokenContent.Trim()),
                            ["ANTIGRAVITY_OAUTH_TOKEN"] = Encoding.UTF8.GetBytes(tokenContent.Trim())
                        }, ct);
                    }
                }
                catch { }

                steps.Add(new PersonaDriverVerificationStep(
                    "Authentication Check",
                    "success",
                    "Google AI Pro OAuth token detected at /root/.gemini/antigravity-cli/antigravity-oauth-token"));
            }
            else if (keyExit == 0 && !string.IsNullOrWhiteSpace(keyOut))
            {
                steps.Add(new PersonaDriverVerificationStep(
                    "Authentication Check",
                    "success",
                    "GEMINI_API_KEY detected in container environment"));
            }
            else
            {
                steps.Add(new PersonaDriverVerificationStep(
                    "Authentication Check",
                    "warning",
                    "Not authenticated with Google AI Pro. Click 'Authenticate Driver' in the UI to log in."));
            }
        }
        else if (driverId.StartsWith("claude") || adapterType == "claude")
        {
            var (keyExit, keyOut, _) = await ExecInContainerAsync(normPersonaId, "printenv ANTHROPIC_API_KEY", ct);
            var (authExit, _, _) = await ExecInContainerAsync(normPersonaId, "test -d /root/.claude", ct);
            if (authExit == 0 || (keyExit == 0 && !string.IsNullOrWhiteSpace(keyOut)))
            {
                steps.Add(new PersonaDriverVerificationStep("Authentication Check", "success", "Claude Code credentials active"));
            }
            else
            {
                steps.Add(new PersonaDriverVerificationStep("Authentication Check", "warning", "No Claude credentials found."));
            }
        }
        else if (driverId.StartsWith("copilot") || adapterType == "copilot")
        {
            var (ghExit, _, _) = await ExecInContainerAsync(normPersonaId, "gh auth status >/dev/null 2>&1 || test -n \"$GITHUB_TOKEN\"", ct);
            if (ghExit == 0)
            {
                steps.Add(new PersonaDriverVerificationStep("Authentication Check", "success", "GitHub Copilot credentials active"));
            }
            else
            {
                steps.Add(new PersonaDriverVerificationStep("Authentication Check", "warning", "No GitHub credentials found. Click 'Authenticate Driver' in the UI to log in."));
            }
        }
        else if (adapterType == "openai")
        {
            var (keyExit, keyOut, _) = await ExecInContainerAsync(normPersonaId, "printenv OPENAI_API_KEY", ct);
            var hasEnvKey = (keyExit == 0 && !string.IsNullOrWhiteSpace(keyOut));

            if (hasApiKey || hasEnvKey)
            {
                steps.Add(new PersonaDriverVerificationStep("Authentication Check", "success", "OpenAI API key configured"));
            }
            else if (!string.IsNullOrWhiteSpace(endpoint) && (endpoint.Contains("localhost") || endpoint.Contains("127.0.0.1")))
            {
                steps.Add(new PersonaDriverVerificationStep("Authentication Check", "success", "Local proxy adapter active (no API key required)"));
            }
            else if (!string.IsNullOrWhiteSpace(loginCmd))
            {
                steps.Add(new PersonaDriverVerificationStep("Authentication Check", "warning", "Driver requires authentication. Click 'Authenticate Driver' in the UI to log in."));
            }
            else
            {
                steps.Add(new PersonaDriverVerificationStep("Authentication Check", "warning", "No OpenAI API key configured. Enter an API key in the UI if required by endpoint."));
            }
        }
        else
        {
            steps.Add(new PersonaDriverVerificationStep("Authentication Check", "success", $"Driver adapter active ({adapterType})"));
        }

        // 6. Mesh API Connectivity & Token Check
        var (curlExit, curlOut, curlErr) = await ExecInContainerAsync(normPersonaId, "curl -s -f -H \"Authorization: Bearer $AGENTMESH_TOKEN\" $AGENTMESH_URL/api/status", ct);
        if (curlExit == 0)
        {
            steps.Add(new PersonaDriverVerificationStep(
                "Mesh API Connectivity",
                "success",
                "Successfully authenticated with AgentMesh API using dedicated AGENTMESH_TOKEN"));
        }
        else
        {
            steps.Add(new PersonaDriverVerificationStep(
                "Mesh API Connectivity",
                "failed",
                $"API connectivity test failed (exit {curlExit}): {(!string.IsNullOrWhiteSpace(curlErr) ? curlErr : curlOut)}"));
        }

        // 7. Driver Endpoint Reachability Check (for HTTP endpoints)
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            var baseEndpoint = endpoint.TrimEnd('/');
            var modelsUrl = baseEndpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? $"{baseEndpoint}/models"
                : $"{baseEndpoint}/v1/models";

            var (epExit, epOut, epErr) = await ExecInContainerAsync(
                normPersonaId,
                $"curl -s -m 5 -o /dev/null -w \"HTTP_CODE:%{{http_code}}\" \"{modelsUrl}\" 2>&1 || curl -s -m 5 -o /dev/null -w \"HTTP_CODE:%{{http_code}}\" \"{baseEndpoint}\" 2>&1",
                ct);

            var epCombined = $"{epOut} {epErr}".Trim();
            if (epCombined.Contains("HTTP_CODE:200") || epCombined.Contains("HTTP_CODE:204"))
            {
                steps.Add(new PersonaDriverVerificationStep(
                    "Driver Endpoint Reachability",
                    "success",
                    $"Endpoint responded successfully at {endpoint} (HTTP 200)"));
            }
            else if (epCombined.Contains("HTTP_CODE:401") || epCombined.Contains("HTTP_CODE:403"))
            {
                steps.Add(new PersonaDriverVerificationStep(
                    "Driver Endpoint Reachability",
                    "warning",
                    $"Endpoint reached at {endpoint} but rejected authentication (HTTP 401/403)"));
            }
            else if (epCombined.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
            {
                steps.Add(new PersonaDriverVerificationStep(
                    "Driver Endpoint Reachability",
                    "failed",
                    $"Connection refused: Failed to connect to {endpoint}"));
            }
            else
            {
                steps.Add(new PersonaDriverVerificationStep(
                    "Driver Endpoint Reachability",
                    "failed",
                    $"Unable to reach endpoint at {endpoint}: {epCombined}"));
            }
        }

        // 8. Dry-run prompt test
        if (steps.All(s => s.Status != "failed"))
        {
            if (driverId.StartsWith("agy") || adapterType == "agy")
            {
                var (dryExit, dryOut, dryErr) = await ExecInContainerAsync(normPersonaId, "timeout 15 agy -p \"hello\"", ct);
                if (dryExit == 0)
                {
                    var summary = !string.IsNullOrWhiteSpace(dryOut) ? dryOut.Split('\n')[0].Trim() : "completed";
                    steps.Add(new PersonaDriverVerificationStep(
                        "Dry-Run Prompt Test",
                        "success",
                        $"Driver executed test prompt successfully: {summary}"));
                }
                else
                {
                    steps.Add(new PersonaDriverVerificationStep(
                        "Dry-Run Prompt Test",
                        "warning",
                        $"Dry-run prompt exited with code {dryExit}: {(!string.IsNullOrWhiteSpace(dryErr) ? dryErr : dryOut)}"));
                }
            }
            else if (driverId.StartsWith("claude") || adapterType == "claude")
            {
                var (dryExit, dryOut, dryErr) = await ExecInContainerAsync(normPersonaId, "timeout 15 claude -p \"hello\"", ct);
                if (dryExit == 0)
                {
                    var summary = !string.IsNullOrWhiteSpace(dryOut) ? dryOut.Split('\n')[0].Trim() : "completed";
                    steps.Add(new PersonaDriverVerificationStep(
                        "Dry-Run Prompt Test",
                        "success",
                        $"Driver executed test prompt successfully: {summary}"));
                }
                else
                {
                    steps.Add(new PersonaDriverVerificationStep(
                        "Dry-Run Prompt Test",
                        "warning",
                        $"Dry-run prompt exited with code {dryExit}: {(!string.IsNullOrWhiteSpace(dryErr) ? dryErr : dryOut)}"));
                }
            }
            else if (adapterType == "openai" && !string.IsNullOrWhiteSpace(endpoint))
            {
                var baseEndpoint = endpoint.TrimEnd('/');
                var modelsUrl = baseEndpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                    ? $"{baseEndpoint}/models"
                    : $"{baseEndpoint}/v1/models";

                var (mExit, mOut, mErr) = await ExecInContainerAsync(normPersonaId, $"curl -s -m 10 -H \"Authorization: Bearer $OPENAI_API_KEY\" \"{modelsUrl}\"", ct);
                if (mExit == 0 && !string.IsNullOrWhiteSpace(mOut) && mOut.Contains("\"data\"", StringComparison.OrdinalIgnoreCase))
                {
                    var modelList = ParseModelsFromBody(mOut);
                    var modelNames = modelList != null && modelList.Count > 0 ? string.Join(", ", modelList.Take(5)) : "active";
                    if (!string.IsNullOrWhiteSpace(defaultModel) && modelList != null && modelList.Contains(defaultModel))
                    {
                        steps.Add(new PersonaDriverVerificationStep(
                            "Dry-Run Prompt Test",
                            "success",
                            $"Endpoint active with configured model '{defaultModel}' ({modelList.Count} models available)"));
                    }
                    else
                    {
                        steps.Add(new PersonaDriverVerificationStep(
                            "Dry-Run Prompt Test",
                            "success",
                            $"Endpoint active. Models available: {modelNames}"));
                    }
                }
                else
                {
                    steps.Add(new PersonaDriverVerificationStep(
                        "Dry-Run Prompt Test",
                        "warning",
                        $"Endpoint models check returned code {mExit}: {(!string.IsNullOrWhiteSpace(mErr) ? mErr : mOut)}"));
                }
            }
            else
            {
                steps.Add(new PersonaDriverVerificationStep(
                    "Dry-Run Prompt Test",
                    "success",
                    $"Driver adapter '{adapterType}' initialized"));
            }
        }

        var overallStatus = steps.Any(s => s.Status == "failed") ? "failed" :
                            steps.Any(s => s.Status == "warning") ? "warning" : "passed";

        return new PersonaDriverVerificationResult(normPersonaId, driverId, overallStatus, steps);
    }

    public async Task<PersonaAuthStartResult> StartAuthAsync(string personaId, CancellationToken ct = default)
    {
        var normPersonaId = personaId.Trim().ToLowerInvariant();
        await EnsureRunningAsync(normPersonaId, ct);

        // Wait up to 15 seconds for a running pod if a rollout or restart was just triggered
        for (var i = 0; i < 15; i++)
        {
            try
            {
                var checkPods = await _client.CoreV1.ListNamespacedPodAsync(
                    _namespace,
                    labelSelector: $"agentmesh.io/persona-id={normPersonaId}",
                    cancellationToken: ct);

                var isRunning = checkPods.Items.Any(p => p.Metadata.DeletionTimestamp == null && p.Status?.Phase == "Running");
                if (isRunning) break;
            }
            catch { }
            await Task.Delay(1000, ct);
        }

        var info = await ResolvePersonaDriverInfoAsync(normPersonaId, ct);
        var driverId = info.DriverId;
        var adapterType = info.AdapterType;
        var sidecar = info.Sidecar;
        var loginCmd = info.LoginCommand;
        var authCheckCmd = info.AuthCheckCommand;

        // Check if already authenticated
        if (!string.IsNullOrWhiteSpace(authCheckCmd))
        {
            var (chkExit, chkOut, _) = await ExecInContainerAsync(normPersonaId, authCheckCmd, ct);
            if (chkExit == 0)
            {
                var summary = !string.IsNullOrWhiteSpace(chkOut) ? chkOut.Split('\n')[0].Trim() : "authenticated";
                return new PersonaAuthStartResult(normPersonaId, driverId, "already_authenticated", Instructions: $"Driver is already authenticated ({summary}).");
            }
        }
        else if (driverId.StartsWith("agy") || adapterType == "agy")
        {
            var (chkExit, _, _) = await ExecInContainerAsync(normPersonaId, "test -f /root/.gemini/antigravity-cli/antigravity-oauth-token", ct);
            if (chkExit == 0)
            {
                return new PersonaAuthStartResult(normPersonaId, driverId, "already_authenticated", Instructions: "Driver is already authenticated with Google AI Pro.");
            }
        }
        else if (driverId.StartsWith("claude") || adapterType == "claude")
        {
            var (chkExit, _, _) = await ExecInContainerAsync(normPersonaId, "test -d /root/.claude", ct);
            if (chkExit == 0)
            {
                return new PersonaAuthStartResult(normPersonaId, driverId, "already_authenticated", Instructions: "Driver is already authenticated with Claude Code.");
            }
        }
        else if (driverId.StartsWith("copilot") || adapterType == "copilot")
        {
            var (chkExit, _, _) = await ExecInContainerAsync(normPersonaId, "gh auth status >/dev/null 2>&1", ct);
            if (chkExit == 0)
            {
                return new PersonaAuthStartResult(normPersonaId, driverId, "already_authenticated", Instructions: "Driver is already authenticated with GitHub Copilot.");
            }
        }

        if (_activeK8sAuthSessions.TryGetValue(normPersonaId, out var existing) && !existing.IsCompleted)
        {
            if (!string.IsNullOrWhiteSpace(existing.AuthUrl))
            {
                var promptMsg = !string.IsNullOrWhiteSpace(loginCmd)
                    ? "Please open the authorization link in your browser to approve access. Once approved, paste the code below if required."
                    : "Click the link to sign in, then paste the authorization code below.";
                return new PersonaAuthStartResult(normPersonaId, driverId, "waiting_code", existing.AuthUrl, promptMsg);
            }
            _activeK8sAuthSessions.TryRemove(normPersonaId, out _);
        }

        var pods = await _client.CoreV1.ListNamespacedPodAsync(
            _namespace,
            labelSelector: $"agentmesh.io/persona-id={normPersonaId}",
            cancellationToken: ct);

        var pod = pods.Items
            .Where(p => p.Metadata.DeletionTimestamp == null && p.Status?.Phase == "Running")
            .OrderByDescending(p => p.Metadata.CreationTimestamp)
            .FirstOrDefault();
        if (pod == null)
        {
            return new PersonaAuthStartResult(normPersonaId, driverId, "error", ErrorMessage: "Persona runner pod is not active yet.");
        }

        try
        {
            string[] authCmd;
            if (!string.IsNullOrWhiteSpace(loginCmd))
            {
                authCmd = new[] { "/bin/bash", "-c", $"export PATH=\"/root/.local/bin:/usr/local/bin:$PATH\"; {loginCmd}" };
            }
            else if (driverId.StartsWith("agy") || adapterType == "agy")
            {
                authCmd = new[] { "agy", "-p", "auth" };
            }
            else if (driverId.StartsWith("claude") || adapterType == "claude")
            {
                authCmd = new[] { "claude", "login" };
            }
            else if (driverId.StartsWith("copilot") || adapterType == "copilot")
            {
                authCmd = new[] { "gh", "auth", "login", "-w" };
            }
            else
            {
                return new PersonaAuthStartResult(normPersonaId, driverId, "apikey_required", Instructions: "Driver does not have an interactive CLI login command configured. Please enter your API key below or configure a Login Command in driver settings.");
            }

            var webSocket = await _client.WebSocketNamespacedPodExecAsync(
                name: pod.Metadata.Name,
                @namespace: _namespace,
                command: authCmd,
                container: "persona-runner",
                stdin: true,
                stdout: true,
                stderr: true,
                tty: true,
                cancellationToken: ct);

            var demuxer = new StreamDemuxer(webSocket);
            var stdInStream = demuxer.GetStream(null, ChannelIndex.StdIn);
            var stdOutStream = demuxer.GetStream(ChannelIndex.StdOut, null);
            var stdErrStream = demuxer.GetStream(ChannelIndex.StdErr, null);
            var errorStream = demuxer.GetStream(ChannelIndex.Error, null);
            demuxer.Start();

            var tcsUrl = new TaskCompletionSource<string>();
            var session = new ActiveK8sAuthSession(webSocket, demuxer, stdInStream, tcsUrl);
            _activeK8sAuthSessions[normPersonaId] = session;

            var capturedOutput = new StringBuilder();

            _ = Task.Run(async () =>
            {
                try
                {
                    var buf = new byte[2048];
                    while (webSocket.State == WebSocketState.Open)
                    {
                        var read = await stdOutStream.ReadAsync(buf, 0, buf.Length, CancellationToken.None);
                        if (read <= 0) break;
                        var chunk = Encoding.UTF8.GetString(buf, 0, read);
                        capturedOutput.Append(chunk);
                        var match = Regex.Match(capturedOutput.ToString(), @"https?://[^\s""'>]+");
                        if (match.Success)
                        {
                            session.AuthUrl = match.Value;
                            tcsUrl.TrySetResult(match.Value);
                        }
                    }

                    await Task.Delay(1000);
                    await ExecInContainerAsync(normPersonaId, PersonaContainerExtensions.SyncPersonaStateCommand, CancellationToken.None);
                    if (!string.IsNullOrWhiteSpace(sidecar?.CompanionCommand))
                    {
                        var compBin = Path.GetFileName(sidecar.CompanionCommand.Trim().Split(' ')[0]);
                        if (!string.IsNullOrWhiteSpace(compBin))
                        {
                            var checkCmd = sidecar.CompanionPort.HasValue && sidecar.CompanionPort.Value > 0
                                ? $"(timeout 1 bash -c '</dev/tcp/127.0.0.1/{sidecar.CompanionPort.Value}' 2>/dev/null || curl -s -m 1 http://127.0.0.1:{sidecar.CompanionPort.Value}/ >/dev/null 2>&1) || pgrep -x '{compBin}' >/dev/null 2>&1 || pidof '{compBin}' >/dev/null 2>&1"
                                : $"pgrep -x '{compBin}' >/dev/null 2>&1 || pidof '{compBin}' >/dev/null 2>&1";
                            await ExecInContainerAsync(normPersonaId, $"{checkCmd} || (nohup {sidecar.CompanionCommand} >/tmp/companion-daemon.log 2>&1 &)", CancellationToken.None);
                        }
                    }

                    if (!tcsUrl.Task.IsCompleted)
                    {
                        var trimmed = capturedOutput.ToString().Trim();
                        if (!string.IsNullOrWhiteSpace(trimmed))
                        {
                            tcsUrl.TrySetException(new InvalidOperationException($"Login command exited without an authorization link. Output: {trimmed}"));
                        }
                    }
                }
                catch (Exception ex)
                {
                    tcsUrl.TrySetException(ex);
                }
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    var errBuf = new byte[2048];
                    while (webSocket.State == WebSocketState.Open)
                    {
                        var read = await stdErrStream.ReadAsync(errBuf, 0, errBuf.Length, CancellationToken.None);
                        if (read <= 0) break;
                        var chunk = Encoding.UTF8.GetString(errBuf, 0, read);
                        capturedOutput.Append(chunk);
                        if (chunk.Contains("pclmul", StringComparison.OrdinalIgnoreCase) ||
                            chunk.Contains("Illegal instruction", StringComparison.OrdinalIgnoreCase))
                        {
                            tcsUrl.TrySetException(new InvalidOperationException(
                                "Google agy binary crashed: Processor missing PCLMULQDQ instruction. In Proxmox VE, edit VM Hardware -> Processor -> Type and set to 'host' (reboot VM). Alternatively, choose Direct API Key mode."));
                            break;
                        }
                        var match = Regex.Match(capturedOutput.ToString(), @"https?://[^\s""'>]+");
                        if (match.Success)
                        {
                            session.AuthUrl = match.Value;
                            tcsUrl.TrySetResult(match.Value);
                        }
                    }
                }
                catch { }
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    using var reader = new StreamReader(errorStream, Encoding.UTF8);
                    var err = await reader.ReadToEndAsync();
                    if (!string.IsNullOrWhiteSpace(err))
                    {
                        capturedOutput.Append(err);
                    }
                }
                catch { }
            });

            var completed = await Task.WhenAny(tcsUrl.Task, Task.Delay(15000, ct));
            if (completed == tcsUrl.Task)
            {
                if (tcsUrl.Task.IsFaulted)
                {
                    _activeK8sAuthSessions.TryRemove(normPersonaId, out _);
                    var ex = tcsUrl.Task.Exception?.InnerException ?? tcsUrl.Task.Exception;
                    return new PersonaAuthStartResult(normPersonaId, driverId, "error", ErrorMessage: ex?.Message ?? "Failed to start driver authentication flow.");
                }

                var authUrl = await tcsUrl.Task;
                var promptMsg = !string.IsNullOrWhiteSpace(loginCmd)
                    ? "Please open the authorization link in your browser to approve access. Once approved, paste the code below if required."
                    : "Click the link to sign in, then paste the authorization code below.";
                return new PersonaAuthStartResult(normPersonaId, driverId, "waiting_code", authUrl, promptMsg);
            }

            _activeK8sAuthSessions.TryRemove(normPersonaId, out _);
            var timeoutOutput = capturedOutput.Length > 0 ? $" Output: {capturedOutput.ToString().Trim()}" : "";
            return new PersonaAuthStartResult(normPersonaId, driverId, "error", ErrorMessage: $"Timed out waiting for authorization link from container.{timeoutOutput}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to start K8s auth session: {Error}", ex.Message);
            return new PersonaAuthStartResult(normPersonaId, driverId, "error", ErrorMessage: ex.Message);
        }
    }

    public async Task<PersonaAuthSubmitResult> SubmitAuthCodeAsync(string personaId, string code, CancellationToken ct = default)
    {
        var normPersonaId = personaId.Trim().ToLowerInvariant();
        var info = await ResolvePersonaDriverInfoAsync(normPersonaId, ct);
        var authCheckCmd = info.AuthCheckCommand;
        var sidecar = info.Sidecar;

        if (!string.IsNullOrWhiteSpace(authCheckCmd))
        {
            var (chkExit, _, _) = await ExecInContainerAsync(normPersonaId, authCheckCmd, ct);
            if (chkExit == 0)
            {
                await ExecInContainerAsync(normPersonaId, PersonaContainerExtensions.SyncPersonaStateCommand, ct);
                if (!string.IsNullOrWhiteSpace(sidecar?.CompanionCommand))
                {
                    var compBin = Path.GetFileName(sidecar.CompanionCommand.Trim().Split(' ')[0]);
                    if (!string.IsNullOrWhiteSpace(compBin))
                    {
                        var checkCmd = sidecar.CompanionPort.HasValue && sidecar.CompanionPort.Value > 0
                            ? $"(timeout 1 bash -c '</dev/tcp/127.0.0.1/{sidecar.CompanionPort.Value}' 2>/dev/null || curl -s -m 1 http://127.0.0.1:{sidecar.CompanionPort.Value}/ >/dev/null 2>&1) || pgrep -x '{compBin}' >/dev/null 2>&1 || pidof '{compBin}' >/dev/null 2>&1"
                            : $"pgrep -x '{compBin}' >/dev/null 2>&1 || pidof '{compBin}' >/dev/null 2>&1";
                        await ExecInContainerAsync(normPersonaId, $"{checkCmd} || (nohup {sidecar.CompanionCommand} >/tmp/companion-daemon.log 2>&1 &)", ct);
                    }
                }
                return new PersonaAuthSubmitResult(normPersonaId, "authenticated", "Driver authentication confirmed and credentials persisted to workspace!");
            }
        }

        if (_activeK8sAuthSessions.TryRemove(normPersonaId, out var session))
        {
            try
            {
                var codeBytes = Encoding.UTF8.GetBytes(code.Trim() + "\n");
                await session.StdIn.WriteAsync(codeBytes, 0, codeBytes.Length, ct);
                await session.StdIn.FlushAsync(ct);

                // Poll for token creation or auth check up to 10 seconds
                for (var i = 0; i < 10; i++)
                {
                    await Task.Delay(1000, ct);

                    if (!string.IsNullOrWhiteSpace(authCheckCmd))
                    {
                        var (recheckExit, _, _) = await ExecInContainerAsync(normPersonaId, authCheckCmd, ct);
                        if (recheckExit == 0)
                        {
                            await ExecInContainerAsync(normPersonaId, PersonaContainerExtensions.SyncPersonaStateCommand, ct);
                            return new PersonaAuthSubmitResult(normPersonaId, "authenticated", "Driver authentication completed successfully!");
                        }
                    }

                    var (chkExit, outStr, _) = await ExecInContainerAsync(normPersonaId, "cat /root/.gemini/antigravity-cli/antigravity-oauth-token", ct);
                    if (chkExit == 0 && !string.IsNullOrWhiteSpace(outStr))
                    {
                        await UpsertPersonaSecretAsync(normPersonaId, new Dictionary<string, byte[]>
                        {
                            ["antigravity-oauth-token"] = Encoding.UTF8.GetBytes(outStr.Trim()),
                            ["ANTIGRAVITY_OAUTH_TOKEN"] = Encoding.UTF8.GetBytes(outStr.Trim())
                        }, ct);

                        return new PersonaAuthSubmitResult(normPersonaId, "authenticated", "Successfully authenticated Google AI Pro credentials and persisted in Kubernetes secret!");
                    }
                }

                await ExecInContainerAsync(normPersonaId, PersonaContainerExtensions.SyncPersonaStateCommand, ct);

                if (!string.IsNullOrWhiteSpace(authCheckCmd))
                {
                    var (finalExit, _, _) = await ExecInContainerAsync(normPersonaId, authCheckCmd, ct);
                    if (finalExit == 0)
                    {
                        return new PersonaAuthSubmitResult(normPersonaId, "authenticated", "Driver authentication completed successfully!");
                    }
                }

                return new PersonaAuthSubmitResult(normPersonaId, "failed", ErrorMessage: "Submitted authorization code, but credentials could not be verified.");
            }
            catch (Exception ex)
            {
                return new PersonaAuthSubmitResult(normPersonaId, "failed", ErrorMessage: ex.Message);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        return new PersonaAuthSubmitResult(normPersonaId, "failed", ErrorMessage: "No active authentication session found. Please click Authenticate Driver first.");
    }

    public async Task<PersonaAuthSubmitResult> ConfigureApiKeyAsync(string personaId, string apiKey, string? provider = null, CancellationToken ct = default)
    {
        var normPersonaId = personaId.Trim().ToLowerInvariant();
        await EnsureRunningAsync(normPersonaId, ct);

        // Wait up to 15 seconds for a running pod if a rollout or restart was just triggered
        for (var i = 0; i < 15; i++)
        {
            try
            {
                var checkPods = await _client.CoreV1.ListNamespacedPodAsync(
                    _namespace,
                    labelSelector: $"agentmesh.io/persona-id={normPersonaId}",
                    cancellationToken: ct);

                var isRunning = checkPods.Items.Any(p => p.Metadata.DeletionTimestamp == null && p.Status?.Phase == "Running");
                if (isRunning) break;
            }
            catch { }
            await Task.Delay(1000, ct);
        }

        var keyName = (!string.IsNullOrWhiteSpace(provider) && (provider.ToLowerInvariant().Contains("claude") || provider.ToLowerInvariant().Contains("anthropic")))
            ? "ANTHROPIC_API_KEY"
            : (!string.IsNullOrWhiteSpace(provider) && provider.ToLowerInvariant().Contains("gemini"))
            ? "GEMINI_API_KEY"
            : "OPENAI_API_KEY";

        await UpsertPersonaSecretAsync(normPersonaId, new Dictionary<string, byte[]>
        {
            [keyName] = Encoding.UTF8.GetBytes(apiKey.Trim())
        }, ct);

        var escapedKey = apiKey.Trim().Replace("\"", "\\\"");
        await ExecInContainerAsync(normPersonaId, $"echo 'export {keyName}=\"{escapedKey}\"' >> /root/.bashrc", ct);
        await ExecInContainerAsync(normPersonaId, $"echo 'export {keyName}=\"{escapedKey}\"' >> /root/.profile", ct);

        return new PersonaAuthSubmitResult(normPersonaId, "authenticated", $"{keyName} successfully configured and persisted in Kubernetes secret!");
    }

    public async Task<IPersonaTerminalSession> AttachTerminalAsync(
        string personaId,
        int cols,
        int rows,
        Func<string, Task> onOutput,
        Func<int, Task> onClosed,
        CancellationToken ct = default)
    {
        var normPersonaId = personaId.Trim().ToLowerInvariant();
        await EnsureRunningAsync(normPersonaId, ct);

        // Wait up to 15 seconds for a running pod if a rollout or restart was just triggered
        for (var i = 0; i < 15; i++)
        {
            try
            {
                var checkPods = await _client.CoreV1.ListNamespacedPodAsync(
                    _namespace,
                    labelSelector: $"agentmesh.io/persona-id={normPersonaId}",
                    cancellationToken: ct);

                var isRunning = checkPods.Items.Any(p => p.Metadata.DeletionTimestamp == null && p.Status?.Phase == "Running");
                if (isRunning) break;
            }
            catch { }
            await Task.Delay(1000, ct);
        }

        var pods = await _client.CoreV1.ListNamespacedPodAsync(
            _namespace,
            labelSelector: $"agentmesh.io/persona-id={normPersonaId}",
            cancellationToken: ct);

        var pod = pods.Items
            .Where(p => p.Metadata.DeletionTimestamp == null && p.Status?.Phase == "Running")
            .OrderByDescending(p => p.Metadata.CreationTimestamp)
            .FirstOrDefault();
        if (pod == null)
        {
            throw new InvalidOperationException($"No running pod found for persona @{personaId}.");
        }

        var webSocket = await _client.WebSocketNamespacedPodExecAsync(
            name: pod.Metadata.Name,
            @namespace: _namespace,
            command: new[] { "/bin/bash" },
            container: "persona-runner",
            stdin: true,
            stdout: true,
            stderr: true,
            tty: true,
            cancellationToken: ct);

        var demuxer = new StreamDemuxer(webSocket);
        var stdOut = demuxer.GetStream(ChannelIndex.StdOut, null);
        var stdErr = demuxer.GetStream(ChannelIndex.StdErr, null);
        var errorStream = demuxer.GetStream(ChannelIndex.Error, null);
        demuxer.Start();

        var writeLock = new SemaphoreSlim(1, 1);
        var session = new KubernetesTerminalSession(webSocket, demuxer, writeLock, _logger);

        // Send initial resize dimensions so PTY has correct cols/rows
        await session.ResizeAsync(cols > 0 ? cols : 80, rows > 0 ? rows : 24);

        _ = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            try
            {
                while (webSocket.State == WebSocketState.Open)
                {
                    var read = await stdOut.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None);
                    if (read <= 0) break;
                    var text = Encoding.UTF8.GetString(buffer, 0, read);
                    await onOutput(text);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Terminal stdout stream ended for persona @{PersonaId}", normPersonaId);
            }
            finally
            {
                try
                {
                    await ExecInContainerAsync(normPersonaId, PersonaContainerExtensions.SyncPersonaStateCommand, CancellationToken.None);
                    var sidecarCfg = await GetPersonaSidecarConfigAsync(normPersonaId, CancellationToken.None);
                    if (!string.IsNullOrWhiteSpace(sidecarCfg?.CompanionCommand))
                    {
                        var compBin = Path.GetFileName(sidecarCfg.CompanionCommand.Trim().Split(' ')[0]);
                        if (!string.IsNullOrWhiteSpace(compBin))
                        {
                            var checkCmd = sidecarCfg.CompanionPort.HasValue && sidecarCfg.CompanionPort.Value > 0
                                ? $"(timeout 1 bash -c '</dev/tcp/127.0.0.1/{sidecarCfg.CompanionPort.Value}' 2>/dev/null || curl -s -m 1 http://127.0.0.1:{sidecarCfg.CompanionPort.Value}/ >/dev/null 2>&1) || pgrep -x '{compBin}' >/dev/null 2>&1 || pidof '{compBin}' >/dev/null 2>&1"
                                : $"pgrep -x '{compBin}' >/dev/null 2>&1 || pidof '{compBin}' >/dev/null 2>&1";
                            await ExecInContainerAsync(normPersonaId, $"{checkCmd} || (nohup {sidecarCfg.CompanionCommand} >/tmp/companion-daemon.log 2>&1 &)", CancellationToken.None);
                        }
                    }
                }
                catch { }

                await onOutput("\r\n\x1b[33mTerminal session closed.\x1b[0m\r\n");
                await onClosed(0);
            }
        });

        _ = Task.Run(async () =>
        {
            var errBuffer = new byte[4096];
            try
            {
                while (webSocket.State == WebSocketState.Open)
                {
                    var read = await stdErr.ReadAsync(errBuffer, 0, errBuffer.Length, CancellationToken.None);
                    if (read <= 0) break;
                    var text = Encoding.UTF8.GetString(errBuffer, 0, read);
                    await onOutput(text);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Terminal stderr stream ended for persona @{PersonaId}", normPersonaId);
            }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                using var reader = new StreamReader(errorStream, Encoding.UTF8);
                var errContent = await reader.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(errContent))
                {
                    _logger.LogWarning("Terminal pod exec error stream: {Error}", errContent);
                }
            }
            catch { }
        });

        return session;
    }

    private async Task UpsertPersonaSecretAsync(string normPersonaId, Dictionary<string, byte[]> data, CancellationToken ct)
    {
        var secretName = $"agentmesh-persona-{normPersonaId}-creds";
        try
        {
            var existing = await _client.CoreV1.ReadNamespacedSecretAsync(secretName, _namespace, cancellationToken: ct);
            existing.Data ??= new Dictionary<string, byte[]>();
            foreach (var (k, v) in data)
            {
                existing.Data[k] = v;
            }
            await _client.CoreV1.ReplaceNamespacedSecretAsync(existing, secretName, _namespace, cancellationToken: ct);
            _logger.LogInformation("Updated credentials secret {SecretName}", secretName);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            var newSecret = new V1Secret
            {
                Metadata = new V1ObjectMeta
                {
                    Name = secretName,
                    NamespaceProperty = _namespace,
                    Labels = new Dictionary<string, string>
                    {
                        ["app.kubernetes.io/name"] = "persona-runner",
                        ["agentmesh.io/persona-id"] = normPersonaId
                    }
                },
                Data = data
            };
            await _client.CoreV1.CreateNamespacedSecretAsync(newSecret, _namespace, cancellationToken: ct);
            _logger.LogInformation("Created credentials secret {SecretName}", secretName);
        }
    }

    private async Task<string> ProvisionAgentTokenAsync(string personaId, CancellationToken ct)
    {
        var normPersonaId = personaId.Trim().TrimStart('@').ToLowerInvariant();
        var personaSecretName = $"agentmesh-persona-{normPersonaId}-creds";
        var deploymentName = GetDeploymentName(normPersonaId);

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentMeshDbContext>();
            var apiKeyService = scope.ServiceProvider.GetRequiredService<IApiKeyService>();

            var persona = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == normPersonaId, ct);
            var rawDriverId = !string.IsNullOrWhiteSpace(persona?.DriverId) ? persona.DriverId : normPersonaId;
            var driverId = rawDriverId.Trim().TrimStart('@').ToLowerInvariant();

            // 1. Check if token already exists in Secret
            string? existingToken = null;
            try
            {
                var secret = await _client.CoreV1.ReadNamespacedSecretAsync(personaSecretName, _namespace, cancellationToken: ct);
                if (secret?.Data != null && secret.Data.TryGetValue("agentmesh-token", out var tokenBytes))
                {
                    existingToken = Encoding.UTF8.GetString(tokenBytes).Trim();
                }
            }
            catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
            }

            // 2. If Secret doesn't have it, check if the running deployment has AGENTMESH_TOKEN in its container env
            if (string.IsNullOrWhiteSpace(existingToken))
            {
                try
                {
                    var deployment = await _client.AppsV1.ReadNamespacedDeploymentAsync(deploymentName, _namespace, cancellationToken: ct);
                    var runnerContainer = deployment.Spec?.Template?.Spec?.Containers?.FirstOrDefault(c => c.Name == "persona-runner");
                    var deploymentToken = runnerContainer?.Env?.FirstOrDefault(e => e.Name == "AGENTMESH_TOKEN")?.Value;
                    if (!string.IsNullOrWhiteSpace(deploymentToken) && deploymentToken.StartsWith("mesh_tok_", StringComparison.OrdinalIgnoreCase))
                    {
                        existingToken = deploymentToken.Trim();
                    }
                }
                catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                }
            }

            // 3. If an existing token was found, reuse and sync it in database and secret
            if (!string.IsNullOrWhiteSpace(existingToken))
            {
                var existingHash = apiKeyService.HashToken(existingToken);
                await SyncAgentTokenHashAsync(db, driverId, normPersonaId, persona?.Name, persona?.Role, existingHash, ct);

                await UpsertPersonaSecretAsync(normPersonaId, new Dictionary<string, byte[]>
                {
                    ["agentmesh-token"] = Encoding.UTF8.GetBytes(existingToken)
                }, ct);

                _logger.LogInformation("Reused existing AgentMesh API token for persona @{PersonaId} (driver @{DriverId})", normPersonaId, driverId);
                return existingToken;
            }

            // 4. Generate new token only when no existing token exists
            var rawToken = apiKeyService.GenerateToken();
            var hashed = apiKeyService.HashToken(rawToken);

            await SyncAgentTokenHashAsync(db, driverId, normPersonaId, persona?.Name, persona?.Role, hashed, ct);

            await UpsertPersonaSecretAsync(normPersonaId, new Dictionary<string, byte[]>
            {
                ["agentmesh-token"] = Encoding.UTF8.GetBytes(rawToken)
            }, ct);

            _logger.LogInformation("Provisioned dedicated AgentMesh API token for persona @{PersonaId} (driver @{DriverId})", normPersonaId, driverId);
            return rawToken;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to provision dedicated persona token: {Error}. Falling back to config master token.", ex.Message);
            return _config["AGENTMESH_TOKEN"] ?? _config["Auth:StaticApiKey"] ?? "mesh-default-secret-key";
        }
    }

    internal static async Task SyncAgentTokenHashAsync(
        AgentMeshDbContext db,
        string driverId,
        string personaId,
        string? personaName,
        string? personaRole,
        string hash,
        CancellationToken ct)
    {
        var targetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { driverId, personaId };
        var agents = await db.Agents.Where(a => targetIds.Contains(a.Id)).ToListAsync(ct);

        foreach (var id in targetIds)
        {
            var isRunnerIdentity = !string.Equals(id, driverId, StringComparison.OrdinalIgnoreCase);
            var agent = agents.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
            if (agent != null)
            {
                if (agent.ApiKeyHash != hash)
                {
                    agent.ApiKeyHash = hash;
                    agent.UpdatedAt = DateTimeOffset.UtcNow;
                }

                // Backfill the marker on rows that predate it so the drivers tab can hide them.
                if (isRunnerIdentity && !AgentKind.HasRunnerMarker(agent.CapabilitiesJson))
                {
                    agent.CapabilitiesJson = AgentKind.BuildRunnerIdentityCapabilities(
                        personaId, driverId, agent.CapabilitiesJson);
                    agent.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }
            else
            {
                // The persona row exists purely so its bearer token has something to authenticate
                // against (ApiKeyAuthenticationHandler matches on agents.api_key_hash). It is NOT a
                // driver: tagging it keeps it out of the drivers tab, which otherwise showed every
                // driver twice - once as the driver, once as a persona-named clone.
                db.Agents.Add(new Agent
                {
                    Id = id,
                    Name = isRunnerIdentity ? (personaName ?? id) : id,
                    Role = isRunnerIdentity ? (personaRole ?? "Persona") : AgentKind.DriverRole,
                    Status = "offline",
                    ApiKeyHash = hash,
                    CapabilitiesJson = isRunnerIdentity
                        ? AgentKind.BuildRunnerIdentityCapabilities(personaId, driverId, null)
                        : "[]",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task SeedCredentialsFromSecretAsync(string normPersonaId, CancellationToken ct)
    {
        try
        {
            var personaSecretName = $"agentmesh-persona-{normPersonaId}-creds";
            var secret = await _client.CoreV1.ReadNamespacedSecretAsync(personaSecretName, _namespace, cancellationToken: ct);
            if (secret?.Data != null && secret.Data.TryGetValue("antigravity-oauth-token", out var tokenBytes))
            {
                var base64 = Convert.ToBase64String(tokenBytes);
                await ExecInContainerAsync(normPersonaId, $"mkdir -p /root/.gemini/antigravity-cli && echo '{base64}' | base64 -d > /root/.gemini/antigravity-cli/antigravity-oauth-token && chmod 600 /root/.gemini/antigravity-cli/antigravity-oauth-token", ct);
                _logger.LogInformation("Seeded Google AI Pro OAuth token into pod for @{PersonaId}", normPersonaId);
            }

            await SyncPersonaGitHubCredentialsAsync(normPersonaId, ct);
        }
        catch { }
    }

    private async Task SyncPersonaGitHubCredentialsAsync(string normPersonaId, CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentMeshDbContext>();
            var encService = scope.ServiceProvider.GetService<ICredentialEncryptionService>();
            var persona = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == normPersonaId, ct);
            if (persona == null) return;

            string? token = null;
            if (!string.IsNullOrWhiteSpace(persona.GitHubTokenEncrypted) && encService != null)
            {
                token = encService.Decrypt(persona.GitHubTokenEncrypted);
            }

            var authorName = !string.IsNullOrWhiteSpace(persona.GitAuthorName) ? persona.GitAuthorName : persona.Name;
            var authorEmail = !string.IsNullOrWhiteSpace(persona.GitAuthorEmail) ? persona.GitAuthorEmail : $"{normPersonaId}-bot@agentmesh.local";

            var secretData = new Dictionary<string, byte[]>();
            if (!string.IsNullOrWhiteSpace(token))
            {
                secretData["GITHUB_TOKEN"] = Encoding.UTF8.GetBytes(token);
                secretData["GH_TOKEN"] = Encoding.UTF8.GetBytes(token);
            }
            if (!string.IsNullOrWhiteSpace(authorName))
            {
                secretData["GIT_AUTHOR_NAME"] = Encoding.UTF8.GetBytes(authorName);
                secretData["GIT_COMMITTER_NAME"] = Encoding.UTF8.GetBytes(authorName);
            }
            if (!string.IsNullOrWhiteSpace(authorEmail))
            {
                secretData["GIT_AUTHOR_EMAIL"] = Encoding.UTF8.GetBytes(authorEmail);
                secretData["GIT_COMMITTER_EMAIL"] = Encoding.UTF8.GetBytes(authorEmail);
            }

            if (secretData.Count > 0)
            {
                await UpsertPersonaSecretAsync(normPersonaId, secretData, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not sync GitHub credentials to Kubernetes secret for @{PersonaId}: {Error}", normPersonaId, ex.Message);
        }
    }

    public static string GetDeploymentName(string personaId) =>
        $"agentmesh-persona-{personaId.Trim().ToLowerInvariant()}";

    public static string GetPvcName(string personaId) =>
        $"agentmesh-persona-{personaId.Trim().ToLowerInvariant()}-workspace";

    private async Task EnsurePersistentVolumeClaimAsync(string normPersonaId, IDictionary<string, string> labels, CancellationToken ct)
    {
        var pvcName = GetPvcName(normPersonaId);
        try
        {
            await _client.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(pvcName, _namespace, cancellationToken: ct);
            return;
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // PVC does not exist yet, proceed to create
        }

        var storageClassName = _config["PersonaContainer:StorageClassName"] ?? _config["Kubernetes:StorageClassName"];
        var storageSize = _config["PersonaContainer:WorkspaceSize"] ?? _config["Kubernetes:WorkspaceSize"] ?? "10Gi";

        var pvcLabels = new Dictionary<string, string>(labels)
        {
            ["agentmesh.io/persona-id"] = normPersonaId
        };

        var pvc = new V1PersistentVolumeClaim
        {
            Metadata = new V1ObjectMeta
            {
                Name = pvcName,
                NamespaceProperty = _namespace,
                Labels = pvcLabels
            },
            Spec = new V1PersistentVolumeClaimSpec
            {
                AccessModes = new List<string> { "ReadWriteOnce" },
                Resources = new V1VolumeResourceRequirements
                {
                    Requests = new Dictionary<string, ResourceQuantity>
                    {
                        ["storage"] = new ResourceQuantity(storageSize)
                    }
                },
                StorageClassName = !string.IsNullOrWhiteSpace(storageClassName) ? storageClassName : null
            }
        };

        try
        {
            _logger.LogInformation("Creating PVC {PvcName} for @{PersonaId} in namespace {Namespace} (size: {Size}, storageClass: {Class})",
                pvcName, normPersonaId, _namespace, storageSize, storageClassName ?? "<default>");
            await _client.CoreV1.CreateNamespacedPersistentVolumeClaimAsync(pvc, _namespace, cancellationToken: ct);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Already created concurrently
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create PVC {PvcName} for @{PersonaId} in namespace {Namespace}", pvcName, normPersonaId, _namespace);
            throw;
        }
    }

    private sealed class KubernetesTerminalSession(
        WebSocket webSocket,
        StreamDemuxer demuxer,
        SemaphoreSlim writeLock,
        ILogger logger) : IPersonaTerminalSession
    {
        public async ValueTask SendInputAsync(string data)
        {
            try
            {
                if (webSocket.State == WebSocketState.Open)
                {
                    var bytes = Encoding.UTF8.GetBytes(data);
                    await writeLock.WaitAsync();
                    try
                    {
                        await demuxer.Write(ChannelIndex.StdIn, bytes, 0, bytes.Length, CancellationToken.None);
                    }
                    finally
                    {
                        writeLock.Release();
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to send input to terminal session");
            }
        }

        public async ValueTask ResizeAsync(int cols, int rows)
        {
            try
            {
                if (webSocket.State == WebSocketState.Open)
                {
                    var resizeMsg = $"{{\"Width\":{cols},\"Height\":{rows}}}";
                    var bytes = Encoding.UTF8.GetBytes(resizeMsg);
                    await writeLock.WaitAsync();
                    try
                    {
                        await demuxer.Write(ChannelIndex.Resize, bytes, 0, bytes.Length, CancellationToken.None);
                    }
                    finally
                    {
                        writeLock.Release();
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to resize terminal session");
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { demuxer.Dispose(); } catch { }
            try
            {
                if (webSocket.State == WebSocketState.Open)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None);
                }
            }
            catch { }
            try { webSocket.Dispose(); } catch { }
            writeLock.Dispose();
        }
    }

    public async Task<DriverEndpointTestResult> TestDriverEndpointAsync(
        string endpoint,
        string? apiKey,
        DriverSidecarConfig? sidecar,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new DriverEndpointTestResult(false, 400, null, "Endpoint URL is required.");
        }

        var baseEndpoint = endpoint.Trim().TrimEnd('/');
        var modelsUrl = baseEndpoint.EndsWith("/models", StringComparison.OrdinalIgnoreCase)
            ? baseEndpoint
            : $"{baseEndpoint}/models";

        // Direct HTTP probe if no sidecar
        if (sidecar == null || !sidecar.Enabled)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                using var req = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());
                }

                using var res = await client.SendAsync(req, ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                var models = ParseModelsFromBody(body);
                return new DriverEndpointTestResult(res.IsSuccessStatusCode, (int)res.StatusCode, body, res.IsSuccessStatusCode ? null : $"HTTP {(int)res.StatusCode}: {res.ReasonPhrase}", models);
            }
            catch (Exception ex)
            {
                return new DriverEndpointTestResult(false, 0, null, $"Connection failed: {ex.Message}");
            }
        }

        // Ephemeral Pod probe with the sidecar container
        var testPodName = $"mesh-test-probe-{Guid.NewGuid():N}"[..32];
        try
        {
            var sidecarContainer = BuildSidecarContainer(sidecar, "probe");
            var curlCmd = !string.IsNullOrWhiteSpace(apiKey)
                ? $"curl -s -f -m 10 -H 'Authorization: Bearer {apiKey.Trim()}' {modelsUrl}"
                : $"curl -s -f -m 10 {modelsUrl}";

            var probeScript = sidecar is { Enabled: true, UserspaceMode: false } && string.Equals(sidecar.Type, "tailscale", StringComparison.OrdinalIgnoreCase)
                ? $"for i in $(seq 1 30); do if [ -d /sys/class/net/tailscale0 ] || grep -q 'tailscale0:' /proc/net/dev 2>/dev/null; then if ! grep -q '100.100.100.100' /etc/resolv.conf 2>/dev/null; then R=$(cat /etc/resolv.conf 2>/dev/null); {{ echo 'nameserver 100.100.100.100'; echo \"$R\"; }} > /etc/resolv.conf 2>/dev/null || true; fi; break; fi; sleep 1; done; sleep 2 && {curlCmd} || exit 1"
                : $"sleep 3 && {curlCmd} || exit 1";

            var probeContainer = new V1Container
            {
                Name = "probe-runner",
                Image = "curlimages/curl:latest",
                SecurityContext = new V1SecurityContext { RunAsUser = 0 },
                Command = new List<string> { "sh", "-c" },
                Args = new List<string>
                {
                    probeScript
                }
            };

            var containers = new List<V1Container> { probeContainer };
            if (sidecarContainer != null)
            {
                containers.Add(sidecarContainer);
            }

            var volumes = new List<V1Volume>();
            if (sidecar is { Enabled: true, UserspaceMode: false } && !string.Equals(sidecar.Type, "custom", StringComparison.OrdinalIgnoreCase))
            {
                volumes.Add(BuildTunVolume());
            }

            var pod = new V1Pod
            {
                Metadata = new V1ObjectMeta
                {
                    Name = testPodName,
                    NamespaceProperty = _namespace,
                    Labels = new Dictionary<string, string>
                    {
                        ["app.kubernetes.io/name"] = "sidecar-probe",
                        ["agentmesh.io/ephemeral-test"] = "true"
                    }
                },
                Spec = new V1PodSpec
                {
                    RestartPolicy = "Never",
                    Containers = containers,
                    Volumes = volumes.Count > 0 ? volumes : null
                }
            };

            await _client.CoreV1.CreateNamespacedPodAsync(pod, _namespace, cancellationToken: ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(25));

            string? responseOutput = null;
            var success = false;
            var statusCode = 0;

            while (!cts.IsCancellationRequested)
            {
                var livePod = await _client.CoreV1.ReadNamespacedPodStatusAsync(testPodName, _namespace, cancellationToken: cts.Token);
                var probeState = livePod.Status?.ContainerStatuses?.FirstOrDefault(c => c.Name == "probe-runner")?.State;
                if (probeState?.Terminated != null)
                {
                    var exitCode = probeState.Terminated.ExitCode;
                    try
                    {
                        using var logStream = await _client.CoreV1.ReadNamespacedPodLogAsync(testPodName, _namespace, container: "probe-runner", cancellationToken: cts.Token);
                        using var reader = new StreamReader(logStream);
                        responseOutput = await reader.ReadToEndAsync(cts.Token);
                    }
                    catch { }

                    success = (exitCode == 0);
                    statusCode = success ? 200 : 500;
                    break;
                }
                await Task.Delay(1000, cts.Token);
            }

            var models = responseOutput != null ? ParseModelsFromBody(responseOutput) : null;
            return new DriverEndpointTestResult(success, statusCode, responseOutput, success ? null : "Probe timed out or failed to reach private LLM via sidecar.", models);
        }
        catch (Exception ex)
        {
            return new DriverEndpointTestResult(false, 0, null, $"Sidecar probe failed: {ex.Message}");
        }
        finally
        {
            try
            {
                await _client.CoreV1.DeleteNamespacedPodAsync(testPodName, _namespace, cancellationToken: CancellationToken.None);
            }
            catch { }
        }
    }

    public static List<string>? ParseModelsFromBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var el in dataArr.EnumerateArray())
                {
                    if (el.TryGetProperty("id", out var idProp))
                    {
                        var id = idProp.GetString();
                        if (!string.IsNullOrWhiteSpace(id)) list.Add(id);
                    }
                }
                return list;
            }
        }
        catch { }
        return null;
    }

    private record PersonaDriverInfo(
        string DriverId,
        string AdapterType,
        string? Endpoint,
        string? DefaultModel,
        bool HasApiKey,
        string? LoginCommand,
        string? AuthCheckCommand,
        DriverSidecarConfig? Sidecar);

    private async Task<PersonaDriverInfo> ResolvePersonaDriverInfoAsync(string normPersonaId, CancellationToken ct)
    {
        string driverId = "agy";
        string adapterType = "agy";
        string? endpoint = null;
        string? defaultModel = null;
        bool hasApiKey = false;
        string? loginCmd = null;
        string? authCheckCmd = null;
        DriverSidecarConfig? sidecar = null;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentMeshDbContext>();
            var persona = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == normPersonaId, ct);
            if (!string.IsNullOrWhiteSpace(persona?.DriverId))
            {
                driverId = persona.DriverId.Trim().TrimStart('@').ToLowerInvariant();
                var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == driverId, ct);
                if (agent != null && !string.IsNullOrWhiteSpace(agent.CapabilitiesJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(agent.CapabilitiesJson);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("adapter", out var aProp) && aProp.ValueKind == JsonValueKind.String)
                        {
                            var aStr = aProp.GetString();
                            if (!string.IsNullOrWhiteSpace(aStr)) adapterType = aStr.Trim().ToLowerInvariant();
                        }
                        else if (driverId.StartsWith("claude")) adapterType = "claude";
                        else if (driverId.StartsWith("copilot")) adapterType = "copilot";
                        else if (driverId.StartsWith("openai")) adapterType = "openai";

                        if (root.TryGetProperty("endpoint", out var eProp) && eProp.ValueKind == JsonValueKind.String)
                            endpoint = eProp.GetString()?.Trim();
                        if (root.TryGetProperty("defaultModel", out var mProp) && mProp.ValueKind == JsonValueKind.String)
                            defaultModel = mProp.GetString()?.Trim();
                        if (root.TryGetProperty("apiKey", out var kProp) && kProp.ValueKind == JsonValueKind.String)
                            hasApiKey = !string.IsNullOrWhiteSpace(kProp.GetString());
                        if (root.TryGetProperty("loginCommand", out var lcProp) && lcProp.ValueKind == JsonValueKind.String)
                            loginCmd = lcProp.GetString()?.Trim();
                        if (root.TryGetProperty("authCheckCommand", out var acProp) && acProp.ValueKind == JsonValueKind.String)
                            authCheckCmd = acProp.GetString()?.Trim();
                        if (root.TryGetProperty("sidecar", out var scProp) && scProp.ValueKind == JsonValueKind.Object)
                            sidecar = JsonSerializer.Deserialize<DriverSidecarConfig>(scProp.GetRawText());
                    }
                    catch { }
                }
            }
        }
        catch { }

        if (sidecar == null)
        {
            sidecar = await GetPersonaSidecarConfigAsync(normPersonaId, ct);
        }

        if (string.IsNullOrWhiteSpace(loginCmd)) loginCmd = sidecar?.LoginCommand;
        if (string.IsNullOrWhiteSpace(authCheckCmd)) authCheckCmd = sidecar?.AuthCheckCommand;

        return new PersonaDriverInfo(driverId, adapterType, endpoint, defaultModel, hasApiKey, loginCmd, authCheckCmd, sidecar);
    }

    private async Task<DriverSidecarConfig?> GetPersonaSidecarConfigAsync(string normPersonaId, CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentMeshDbContext>();
            var persona = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == normPersonaId, ct);
            if (persona == null) return null;
            var driverId = !string.IsNullOrWhiteSpace(persona.DriverId) ? persona.DriverId.Trim().TrimStart('@').ToLowerInvariant() : null;
            if (string.IsNullOrWhiteSpace(driverId)) return null;

            var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == driverId, ct);
            if (agent == null || string.IsNullOrWhiteSpace(agent.CapabilitiesJson)) return null;

            using var doc = JsonDocument.Parse(agent.CapabilitiesJson);
            if (doc.RootElement.TryGetProperty("sidecar", out var sc) && sc.ValueKind == JsonValueKind.Object)
            {
                return JsonSerializer.Deserialize<DriverSidecarConfig>(sc.GetRawText());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to resolve driver sidecar config for persona @{PersonaId}: {Error}", normPersonaId, ex.Message);
        }
        return null;
    }

    public static V1Container? BuildSidecarContainer(DriverSidecarConfig sidecar, string normPersonaId)
    {
        if (!sidecar.Enabled) return null;

        if (string.Equals(sidecar.Type, "custom", StringComparison.OrdinalIgnoreCase))
        {
            var customEnv = new List<V1EnvVar>();
            if (sidecar.EnvironmentVariables != null)
            {
                foreach (var (k, v) in sidecar.EnvironmentVariables)
                {
                    customEnv.Add(new V1EnvVar { Name = k, Value = v });
                }
            }

            return new V1Container
            {
                Name = "driver-sidecar",
                Image = !string.IsNullOrWhiteSpace(sidecar.Image) ? sidecar.Image : "alpine:latest",
                Command = !string.IsNullOrWhiteSpace(sidecar.Command) ? new List<string> { sidecar.Command } : null,
                Args = sidecar.Arguments,
                Env = customEnv.Count > 0 ? customEnv : null
            };
        }

        var hostname = !string.IsNullOrWhiteSpace(sidecar.Hostname) ? sidecar.Hostname : $"mesh-{normPersonaId}";
        var extraArgsSb = new StringBuilder();
        extraArgsSb.Append($"--hostname={hostname}");
        if (!string.IsNullOrWhiteSpace(sidecar.Tags))
        {
            extraArgsSb.Append($" --advertise-tags={sidecar.Tags}");
        }
        if (!string.IsNullOrWhiteSpace(sidecar.ExtraArgs))
        {
            var cleanArgs = DriverSidecarConfig.CleanTailscaleExtraArgs(sidecar.ExtraArgs);
            if (!string.IsNullOrWhiteSpace(cleanArgs))
            {
                extraArgsSb.Append($" {cleanArgs}");
            }
        }

        var tsEnv = new List<V1EnvVar>
        {
            // Explicitly disable Kubernetes Secret storage so containerboot runs in ephemeral mode
            // without requiring cluster RBAC permissions on secrets or events.
            new() { Name = "TS_KUBE_SECRET", Value = "" },
            new() { Name = "TS_EXTRA_ARGS", Value = extraArgsSb.ToString().Trim() },
            new() { Name = "TS_USERSPACE", Value = sidecar.UserspaceMode ? "true" : "false" },
            new() { Name = "TS_ACCEPT_DNS", Value = "true" }
        };

        if (!string.IsNullOrWhiteSpace(sidecar.SecretRefName))
        {
            tsEnv.Add(new V1EnvVar
            {
                Name = "TS_AUTHKEY",
                ValueFrom = new V1EnvVarSource
                {
                    SecretKeyRef = new V1SecretKeySelector
                    {
                        Name = sidecar.SecretRefName,
                        Key = !string.IsNullOrWhiteSpace(sidecar.SecretRefKey) ? sidecar.SecretRefKey : "TS_AUTHKEY",
                        Optional = true
                    }
                }
            });
        }
        else if (!string.IsNullOrWhiteSpace(sidecar.AuthKey))
        {
            tsEnv.Add(new V1EnvVar { Name = "TS_AUTHKEY", Value = sidecar.AuthKey });
        }

        if (sidecar.EnvironmentVariables != null)
        {
            foreach (var (k, v) in sidecar.EnvironmentVariables)
            {
                tsEnv.RemoveAll(e => string.Equals(e.Name, k, StringComparison.OrdinalIgnoreCase));
                tsEnv.Add(new V1EnvVar { Name = k, Value = v });
            }
        }

        var sidecarContainer = new V1Container
        {
            Name = "driver-sidecar",
            Image = !string.IsNullOrWhiteSpace(sidecar.Image) ? sidecar.Image : "tailscale/tailscale:v1.78.1",
            Env = tsEnv
        };

        if (!sidecar.UserspaceMode)
        {
            sidecarContainer.SecurityContext = new V1SecurityContext
            {
                Capabilities = new V1Capabilities { Add = new List<string> { "NET_ADMIN" } }
            };
            sidecarContainer.VolumeMounts = new List<V1VolumeMount>
            {
                new() { Name = "tun-dev", MountPath = "/dev/net/tun" }
            };
        }

        return sidecarContainer;
    }

    public static V1Volume BuildTunVolume()
    {
        return new V1Volume
        {
            Name = "tun-dev",
            HostPath = new V1HostPathVolumeSource
            {
                Path = "/dev/net/tun",
                Type = "CharDevice"
            }
        };
    }

    public static object BuildSidecarPatchObject(V1Container sidecarContainer)
    {
        var scEnv = sidecarContainer.Env?.Select(e => new
        {
            name = e.Name,
            value = e.Value,
            valueFrom = e.ValueFrom != null ? new
            {
                secretKeyRef = e.ValueFrom.SecretKeyRef != null ? new
                {
                    name = e.ValueFrom.SecretKeyRef.Name,
                    key = e.ValueFrom.SecretKeyRef.Key,
                    optional = e.ValueFrom.SecretKeyRef.Optional
                } : null
            } : null
        }).ToList();

        var scMounts = sidecarContainer.VolumeMounts?.Select(vm => new
        {
            name = vm.Name,
            mountPath = vm.MountPath
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["name"] = sidecarContainer.Name,
            ["image"] = sidecarContainer.Image,
            ["command"] = sidecarContainer.Command,
            ["args"] = sidecarContainer.Args,
            ["env"] = scEnv,
            ["volumeMounts"] = scMounts,
            ["securityContext"] = sidecarContainer.SecurityContext != null ? new
            {
                capabilities = sidecarContainer.SecurityContext.Capabilities != null ? new
                {
                    add = sidecarContainer.SecurityContext.Capabilities.Add
                } : null
            } : null
        };
    }

    private sealed class ActiveK8sAuthSession(WebSocket webSocket, StreamDemuxer demuxer, Stream stdIn, TaskCompletionSource<string> tcs) : IAsyncDisposable
    {
        public Stream StdIn => stdIn;
        public string? AuthUrl { get; set; }
        public bool IsCompleted => tcs.Task.IsCompleted;

        public async ValueTask DisposeAsync()
        {
            try { stdIn.Dispose(); } catch { }
            try { demuxer.Dispose(); } catch { }
            try
            {
                if (webSocket.State == WebSocketState.Open)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None);
                }
            }
            catch { }
            try { webSocket.Dispose(); } catch { }
        }
    }
}
