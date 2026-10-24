using System.Text.Json;
using AgentMesh.Core.Services;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentMesh.Infrastructure.Containers;

public class KubernetesPersonaContainerManager : IPersonaContainerManager
{
    private readonly ILogger<KubernetesPersonaContainerManager> _logger;
    private readonly IConfiguration _config;
    private readonly IKubernetes _client;
    private readonly string _namespace;
    private readonly string _defaultImage;

    public string RuntimeName => "kubernetes";

    public KubernetesPersonaContainerManager(
        ILogger<KubernetesPersonaContainerManager> logger,
        IConfiguration config)
    {
        _logger = logger;
        _config = config;
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

            return new PersonaEnvironmentStatus(
                PersonaId: personaId,
                Status: status,
                Runtime: RuntimeName,
                ContainerId: deploymentName,
                StartedAt: deployment.Metadata.CreationTimestamp,
                LastActivityAt: DateTimeOffset.UtcNow,
                ErrorMessage: errorMessage);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new PersonaEnvironmentStatus(personaId, "stopped", RuntimeName);
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

        var currentConfigImage = _config["PersonaContainer:Image"] ?? _defaultImage;
        var currentPullPolicy = _config["PersonaContainer:PullPolicy"];
        var currentImagePullSecret = _config["PersonaContainer:ImagePullSecret"];

        var meshUrl = _config["Kubernetes:ApiUrl"] ??
                      _config["AGENTMESH_URL"] ??
                      "http://agent-mesh-api:5000";

        var labels = new Dictionary<string, string>
        {
            ["app.kubernetes.io/name"] = "persona-runner",
            ["app.kubernetes.io/component"] = "persona-runner",
            ["agentmesh.io/persona-id"] = normPersonaId
        };

        var env = new List<V1EnvVar>
        {
            new() { Name = "AGENTMESH_URL", Value = meshUrl },
            new() { Name = "AGENTMESH_PERSONA_ID", Value = normPersonaId }
        };

        var envFrom = new List<V1EnvFromSource>();
        var secretName = _config["Kubernetes:SecretName"] ?? "agent-mesh-secrets";
        envFrom.Add(new V1EnvFromSource { SecretRef = new V1SecretEnvSource { Name = secretName, Optional = true } });

        try
        {
            var deployment = await _client.AppsV1.ReadNamespacedDeploymentAsync(deploymentName, _namespace, cancellationToken: ct);
            var runnerContainer = deployment.Spec?.Template?.Spec?.Containers?.FirstOrDefault(c => c.Name == "persona-runner");
            var currentImage = runnerContainer?.Image;
            var currentPull = runnerContainer?.ImagePullPolicy;
            var currentPersonaEnv = runnerContainer?.Env?.FirstOrDefault(e => e.Name == "AGENTMESH_PERSONA_ID")?.Value;
            var currentUrlEnv = runnerContainer?.Env?.FirstOrDefault(e => e.Name == "AGENTMESH_URL")?.Value;
            var hasWorkspaceMount = runnerContainer?.VolumeMounts?.Any(vm => vm.MountPath == "/workspace") == true;
            var hasWorkspaceVolume = deployment.Spec?.Template?.Spec?.Volumes?.Any(v => v.Name == "workspace-storage") == true;

            var isScaledDown = (deployment.Spec?.Replicas ?? 0) == 0;
            var needsImageUpdate = runnerContainer != null && !string.Equals(currentImage, currentConfigImage, StringComparison.OrdinalIgnoreCase);
            var needsPullPolicyUpdate = !string.IsNullOrWhiteSpace(currentPullPolicy) && !string.Equals(currentPull, currentPullPolicy, StringComparison.OrdinalIgnoreCase);
            var needsEnvUpdate = runnerContainer == null ||
                                 !string.Equals(currentPersonaEnv, normPersonaId, StringComparison.OrdinalIgnoreCase) ||
                                 !string.Equals(currentUrlEnv, meshUrl, StringComparison.OrdinalIgnoreCase);
            var needsVolumeUpdate = !hasWorkspaceMount || !hasWorkspaceVolume;

            // Query status to detect crashed/error pods that need a kick
            var currentStatus = await GetStatusAsync(personaId, ct);
            var isFailed = currentStatus.Status == "error";

            if (isScaledDown || needsImageUpdate || needsPullPolicyUpdate || needsEnvUpdate || needsVolumeUpdate || isFailed)
            {
                _logger.LogInformation("Updating deployment {Name} for @{PersonaId}: Replicas=1, Image={Image} (was {OldImage}), EnvUpdated={EnvUpdated}, IsFailed={IsFailed}",
                    deploymentName, personaId, currentConfigImage, currentImage, needsEnvUpdate, isFailed);

                var containerPatch = new Dictionary<string, object>
                {
                    ["name"] = "persona-runner",
                    ["image"] = currentConfigImage,
                    ["env"] = env.Select(e => new { name = e.Name, value = e.Value }).ToList(),
                    ["envFrom"] = envFrom.Select(ef => new
                    {
                        secretRef = new { name = ef.SecretRef?.Name, optional = ef.SecretRef?.Optional }
                    }).ToList(),
                    ["volumeMounts"] = new[]
                    {
                        new { name = "workspace-storage", mountPath = "/workspace" }
                    }
                };

                if (!string.IsNullOrWhiteSpace(currentPullPolicy))
                {
                    containerPatch["imagePullPolicy"] = currentPullPolicy;
                }

                var podSpecPatch = new Dictionary<string, object>
                {
                    ["containers"] = new[] { containerPatch },
                    ["volumes"] = new[]
                    {
                        new { name = "workspace-storage", emptyDir = new { } }
                    }
                };

                if (!string.IsNullOrWhiteSpace(currentImagePullSecret))
                {
                    podSpecPatch["imagePullSecrets"] = new[]
                    {
                        new { name = currentImagePullSecret }
                    };
                }

                var patchDoc = new
                {
                    spec = new
                    {
                        replicas = 1,
                        template = new
                        {
                            metadata = new
                            {
                                labels = labels,
                                annotations = new Dictionary<string, string>
                                {
                                    ["kubectl.kubernetes.io/restartedAt"] = DateTimeOffset.UtcNow.ToString("o")
                                }
                            },
                            spec = podSpecPatch
                        }
                    }
                };

                var patchJson = JsonSerializer.Serialize(patchDoc);
                var patch = new V1Patch(patchJson, V1Patch.PatchType.StrategicMergePatch);
                await _client.AppsV1.PatchNamespacedDeploymentAsync(patch, deploymentName, _namespace, cancellationToken: ct);
            }
            return;
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Create deployment
        }

        _logger.LogInformation("Creating new deployment {Name} for @{PersonaId} in namespace {Namespace} with image {Image}", deploymentName, personaId, _namespace, currentConfigImage);

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
                Selector = new V1LabelSelector { MatchLabels = labels },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta { Labels = labels },
                    Spec = new V1PodSpec
                    {
                        ImagePullSecrets = !string.IsNullOrWhiteSpace(currentImagePullSecret)
                            ? new List<V1LocalObjectReference> { new() { Name = currentImagePullSecret } }
                            : null,
                        Containers = new List<V1Container>
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
                                    new() { MountPath = "/workspace", Name = "workspace-storage" }
                                }
                            }
                        },
                        Volumes = new List<V1Volume>
                        {
                            new() { Name = "workspace-storage", EmptyDir = new V1EmptyDirVolumeSource() }
                        }
                    }
                }
            }
        };

        await _client.AppsV1.CreateNamespacedDeploymentAsync(newDeployment, _namespace, cancellationToken: ct);
        _logger.LogInformation("Deployment {Name} created successfully for @{PersonaId}", deploymentName, personaId);
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

    public async Task<string> GetLogsAsync(string personaId, int tailLines = 100, CancellationToken ct = default)
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
            using var stream = await _client.CoreV1.ReadNamespacedPodLogAsync(
                pod.Metadata.Name,
                _namespace,
                tailLines: tailLines,
                cancellationToken: ct);

            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to read pod logs for @{PersonaId}: {Error}", personaId, ex.Message);
            return $"Error reading logs: {ex.Message}";
        }
    }

    private static string GetDeploymentName(string personaId) =>
        $"agentmesh-persona-{personaId.Trim().ToLowerInvariant()}";
}
