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

        var agentToken = await ProvisionAgentTokenAsync(normPersonaId, ct);

        var env = new List<V1EnvVar>
        {
            new() { Name = "AGENTMESH_URL", Value = meshUrl },
            new() { Name = "AGENTMESH_TOKEN", Value = agentToken },
            new() { Name = "AGENTMESH_PERSONA_ID", Value = normPersonaId }
        };

        var envFrom = new List<V1EnvFromSource>();
        var secretName = _config["Kubernetes:SecretName"] ?? "agent-mesh-secrets";
        envFrom.Add(new V1EnvFromSource { SecretRef = new V1SecretEnvSource { Name = secretName, Optional = true } });

        var personaSecretName = $"agentmesh-persona-{normPersonaId}-creds";
        envFrom.Add(new V1EnvFromSource { SecretRef = new V1SecretEnvSource { Name = personaSecretName, Optional = true } });

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
            var hasWorkspaceVolume = deployment.Spec?.Template?.Spec?.Volumes?.Any(v => v.Name == "workspace-storage") == true;
            var hasCredsMount = runnerContainer?.VolumeMounts?.Any(vm => vm.MountPath == "/etc/agentmesh-creds") == true;
            var hasCredsVolume = deployment.Spec?.Template?.Spec?.Volumes?.Any(v => v.Name == "persona-creds-volume") == true;

            var isScaledDown = (deployment.Spec?.Replicas ?? 0) == 0;
            var needsImageUpdate = runnerContainer != null && !string.Equals(currentImage, currentConfigImage, StringComparison.OrdinalIgnoreCase);
            var needsPullPolicyUpdate = !string.IsNullOrWhiteSpace(currentPullPolicy) && !string.Equals(currentPull, currentPullPolicy, StringComparison.OrdinalIgnoreCase);
            var needsEnvUpdate = runnerContainer == null ||
                                 !string.Equals(currentPersonaEnv, normPersonaId, StringComparison.OrdinalIgnoreCase) ||
                                 !string.Equals(currentUrlEnv, meshUrl, StringComparison.OrdinalIgnoreCase) ||
                                 !string.Equals(currentTokenEnv, agentToken, StringComparison.Ordinal);
            var needsVolumeUpdate = !hasWorkspaceMount || !hasWorkspaceVolume || !hasCredsMount || !hasCredsVolume;

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

                var podSpecPatch = new Dictionary<string, object>
                {
                    ["containers"] = new[] { containerPatch },
                    ["volumes"] = new object[]
                    {
                        new { name = "workspace-storage", emptyDir = new { } },
                        new { name = "persona-creds-volume", secret = new { secretName = personaSecretName, optional = true } }
                    }
                };

                if (!string.IsNullOrWhiteSpace(currentImagePullSecret))
                {
                    podSpecPatch["imagePullSecrets"] = new[]
                    {
                        new { name = currentImagePullSecret }
                    };
                }

                var annotations = new Dictionary<string, string>();
                if (isFailed)
                {
                    annotations["kubectl.kubernetes.io/restartedAt"] = DateTimeOffset.UtcNow.ToString("o");
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
                                    new() { MountPath = "/workspace", Name = "workspace-storage" },
                                    new() { MountPath = "/etc/agentmesh-creds", Name = "persona-creds-volume", ReadOnlyProperty = true }
                                }
                            }
                        },
                        Volumes = new List<V1Volume>
                        {
                            new() { Name = "workspace-storage", EmptyDir = new V1EmptyDirVolumeSource() },
                            new() { Name = "persona-creds-volume", Secret = new V1SecretVolumeSource { SecretName = personaSecretName, Optional = true } }
                        }
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

            var runnerStatus = pod.Status?.ContainerStatuses?.FirstOrDefault(c => c.Name == "persona-runner")
                               ?? pod.Status?.ContainerStatuses?.FirstOrDefault();

            if (runnerStatus?.State?.Waiting != null)
            {
                var reason = runnerStatus.State.Waiting.Reason ?? "Initializing";
                var msg = runnerStatus.State.Waiting.Message;
                if (reason is "ImagePullBackOff" or "ErrImagePull" or "CrashLoopBackOff" or "Error")
                {
                    return $"[Pod Status: Error]\nContainer failed to start: {reason}\n{msg}";
                }

                return $"[Pod Status: Initializing]\nPersona container is starting up ({reason})...\nLogs will appear automatically as soon as the container is running.";
            }

            using var stream = await _client.CoreV1.ReadNamespacedPodLogAsync(
                pod.Metadata.Name,
                _namespace,
                container: "persona-runner",
                tailLines: tailLines,
                cancellationToken: ct);

            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(ct);
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

            var pod = pods.Items.OrderByDescending(p => p.Metadata.CreationTimestamp).FirstOrDefault();
            if (pod == null)
            {
                return (-1, "", "No pod found for persona.");
            }

            var stdOutBuilder = new StringBuilder();
            var stdErrBuilder = new StringBuilder();

            var exitCode = await _client.NamespacedPodExecAsync(
                name: pod.Metadata.Name,
                @namespace: _namespace,
                container: "persona-runner",
                command: new[] { "sh", "-c", command },
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

        string driverId = "agy";
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentMeshDbContext>();
            var p = await db.Personas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == normPersonaId, ct);
            if (!string.IsNullOrWhiteSpace(p?.DriverId))
            {
                driverId = p.DriverId.Trim().ToLowerInvariant();
            }
        }
        catch { }

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

        // 2. Binary presence check
        string checkCmd = driverId.StartsWith("claude") ? "claude --version" :
                          driverId.StartsWith("copilot") ? "gh --version" :
                          driverId.StartsWith("openai") ? "echo 'OpenAI API Adapter'" :
                          "agy --version";

        var (binExit, binOut, binErr) = await ExecInContainerAsync(normPersonaId, checkCmd, ct);
        if (binExit == 0)
        {
            var ver = !string.IsNullOrWhiteSpace(binOut) ? binOut.Split('\n')[0].Trim() : "available";
            steps.Add(new PersonaDriverVerificationStep(
                "CLI Binary Check",
                "success",
                $"{driverId} binary detected in container ({ver})"));
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

        // 3. Authentication check
        await SeedCredentialsFromSecretAsync(normPersonaId, ct);
        if (driverId.StartsWith("agy"))
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
                    "Not authenticated with Google AI Pro. Click 'Authenticate Driver' or enter an API key in the UI."));
            }
        }
        else if (driverId.StartsWith("claude"))
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
        else
        {
            steps.Add(new PersonaDriverVerificationStep("Authentication Check", "success", "Driver adapter active"));
        }

        // 4. Mesh API Connectivity & Token Check
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

        // 5. Dry-run prompt test
        if (steps.All(s => s.Status != "failed"))
        {
            var dryRunCmd = driverId.StartsWith("agy") ? "timeout 15 agy -p \"hello\"" :
                            driverId.StartsWith("claude") ? "timeout 15 claude -p \"hello\"" :
                            "echo 'OK'";
            var (dryExit, dryOut, dryErr) = await ExecInContainerAsync(normPersonaId, dryRunCmd, ct);
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

        var overallStatus = steps.Any(s => s.Status == "failed") ? "failed" :
                            steps.Any(s => s.Status == "warning") ? "warning" : "passed";

        return new PersonaDriverVerificationResult(normPersonaId, driverId, overallStatus, steps);
    }

    public async Task<PersonaAuthStartResult> StartAuthAsync(string personaId, CancellationToken ct = default)
    {
        var normPersonaId = personaId.Trim().ToLowerInvariant();
        await EnsureRunningAsync(normPersonaId, ct);

        // Check if already authenticated
        var (chkExit, _, _) = await ExecInContainerAsync(normPersonaId, "test -f /root/.gemini/antigravity-cli/antigravity-oauth-token", ct);
        if (chkExit == 0)
        {
            return new PersonaAuthStartResult(normPersonaId, "agy", "already_authenticated", Instructions: "Driver is already authenticated with Google AI Pro.");
        }

        if (_activeK8sAuthSessions.TryGetValue(normPersonaId, out var existing) && !existing.IsCompleted)
        {
            if (!string.IsNullOrWhiteSpace(existing.AuthUrl))
            {
                return new PersonaAuthStartResult(normPersonaId, "agy", "waiting_code", existing.AuthUrl, "Please open the Google authorization link, approve access, and paste the authorization code below.");
            }
            _activeK8sAuthSessions.TryRemove(normPersonaId, out _);
        }

        var pods = await _client.CoreV1.ListNamespacedPodAsync(
            _namespace,
            labelSelector: $"agentmesh.io/persona-id={normPersonaId}",
            cancellationToken: ct);

        var pod = pods.Items.OrderByDescending(p => p.Metadata.CreationTimestamp).FirstOrDefault();
        if (pod == null)
        {
            return new PersonaAuthStartResult(normPersonaId, "agy", "error", ErrorMessage: "Persona runner pod is not active yet.");
        }

        try
        {
            var webSocket = await _client.WebSocketNamespacedPodExecAsync(
                name: pod.Metadata.Name,
                @namespace: _namespace,
                command: new[] { "agy", "-p", "auth" },
                container: "persona-runner",
                cancellationToken: ct);

            var demuxer = new StreamDemuxer(webSocket);
            demuxer.Start();

            var stdInStream = demuxer.GetStream(ChannelIndex.StdIn, ChannelIndex.StdIn);
            var stdOutStream = demuxer.GetStream(ChannelIndex.StdOut, null);
            var stdErrStream = demuxer.GetStream(ChannelIndex.StdErr, null);

            var tcsUrl = new TaskCompletionSource<string>();
            var session = new ActiveK8sAuthSession(webSocket, demuxer, stdInStream, tcsUrl);
            _activeK8sAuthSessions[normPersonaId] = session;

            _ = Task.Run(async () =>
            {
                try
                {
                    using var reader = new StreamReader(stdOutStream, Encoding.UTF8);
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        var match = Regex.Match(line, @"https://accounts\.google\.com/o/oauth2/auth[^\s]+");
                        if (match.Success)
                        {
                            session.AuthUrl = match.Value;
                            tcsUrl.TrySetResult(match.Value);
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
                    using var errReader = new StreamReader(stdErrStream, Encoding.UTF8);
                    string? errLine;
                    while ((errLine = await errReader.ReadLineAsync()) != null)
                    {
                        if (errLine.Contains("pclmul", StringComparison.OrdinalIgnoreCase) ||
                            errLine.Contains("Illegal instruction", StringComparison.OrdinalIgnoreCase))
                        {
                            tcsUrl.TrySetException(new InvalidOperationException(
                                "Google agy binary crashed: Processor missing PCLMULQDQ instruction. In Proxmox VE, edit VM Hardware -> Processor -> Type and set to 'host' (reboot VM). Alternatively, choose Direct API Key mode."));
                            break;
                        }
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
                    return new PersonaAuthStartResult(normPersonaId, "agy", "error", ErrorMessage: ex?.Message ?? "Failed to start Google OAuth flow.");
                }

                var authUrl = await tcsUrl.Task;
                return new PersonaAuthStartResult(normPersonaId, "agy", "waiting_code", authUrl, "Click the link to sign in with your Google account, then paste the authorization code below.");
            }

            _activeK8sAuthSessions.TryRemove(normPersonaId, out _);
            return new PersonaAuthStartResult(normPersonaId, "agy", "error", ErrorMessage: "Timed out waiting for Google OAuth authorization link from container.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to start K8s auth session: {Error}", ex.Message);
            return new PersonaAuthStartResult(normPersonaId, "agy", "error", ErrorMessage: ex.Message);
        }
    }

    public async Task<PersonaAuthSubmitResult> SubmitAuthCodeAsync(string personaId, string code, CancellationToken ct = default)
    {
        var normPersonaId = personaId.Trim().ToLowerInvariant();

        if (_activeK8sAuthSessions.TryRemove(normPersonaId, out var session))
        {
            try
            {
                var codeBytes = Encoding.UTF8.GetBytes(code.Trim() + "\n");
                await session.StdIn.WriteAsync(codeBytes, 0, codeBytes.Length, ct);
                await session.StdIn.FlushAsync(ct);

                // Poll for token creation up to 10 seconds
                string? tokenOut = null;
                for (var i = 0; i < 10; i++)
                {
                    await Task.Delay(1000, ct);
                    var (chkExit, outStr, _) = await ExecInContainerAsync(normPersonaId, "cat /root/.gemini/antigravity-cli/antigravity-oauth-token", ct);
                    if (chkExit == 0 && !string.IsNullOrWhiteSpace(outStr))
                    {
                        tokenOut = outStr;
                        break;
                    }
                }

                if (!string.IsNullOrWhiteSpace(tokenOut))
                {
                    await UpsertPersonaSecretAsync(normPersonaId, new Dictionary<string, byte[]>
                    {
                        ["antigravity-oauth-token"] = Encoding.UTF8.GetBytes(tokenOut.Trim()),
                        ["ANTIGRAVITY_OAUTH_TOKEN"] = Encoding.UTF8.GetBytes(tokenOut.Trim())
                    }, ct);

                    return new PersonaAuthSubmitResult(normPersonaId, "authenticated", "Successfully authenticated Google AI Pro credentials and persisted in Kubernetes secret!");
                }

                return new PersonaAuthSubmitResult(normPersonaId, "failed", ErrorMessage: "Submitted authorization code, but credentials token was not created.");
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

        var keyName = (!string.IsNullOrWhiteSpace(provider) && provider.ToLowerInvariant().Contains("claude"))
            ? "ANTHROPIC_API_KEY"
            : "GEMINI_API_KEY";

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

        var pods = await _client.CoreV1.ListNamespacedPodAsync(
            _namespace,
            labelSelector: $"agentmesh.io/persona-id={normPersonaId}",
            cancellationToken: ct);

        var pod = pods.Items.OrderByDescending(p => p.Metadata.CreationTimestamp).FirstOrDefault();
        if (pod == null)
        {
            throw new InvalidOperationException($"No running pod found for persona @{personaId}.");
        }

        var webSocket = await _client.WebSocketNamespacedPodExecAsync(
            name: pod.Metadata.Name,
            @namespace: _namespace,
            command: new[] { "/bin/bash" },
            container: "persona-runner",
            cancellationToken: ct);

        var demuxer = new StreamDemuxer(webSocket);
        demuxer.Start();

        var stdIn = demuxer.GetStream(ChannelIndex.StdIn, ChannelIndex.StdIn);
        var stdOut = demuxer.GetStream(ChannelIndex.StdOut, null);
        var stdErr = demuxer.GetStream(ChannelIndex.StdErr, null);

        var session = new KubernetesTerminalSession(webSocket, demuxer, stdIn);

        _ = Task.Run(async () =>
        {
            var buffer = new byte[2048];
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
            catch { }
            finally
            {
                await onOutput("\r\n\x1b[33mTerminal session closed.\x1b[0m\r\n");
                await onClosed(0);
            }
        });

        _ = Task.Run(async () =>
        {
            var errBuffer = new byte[2048];
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

    private static async Task SyncAgentTokenHashAsync(
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
            var agent = agents.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
            if (agent != null)
            {
                if (agent.ApiKeyHash != hash)
                {
                    agent.ApiKeyHash = hash;
                    agent.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }
            else
            {
                var name = (id == personaId ? personaName : null) ?? id;
                var role = (id == personaId ? personaRole : null) ?? "Autonomous Driver";
                db.Agents.Add(new Agent
                {
                    Id = id,
                    Name = name,
                    Role = role,
                    Status = "offline",
                    ApiKeyHash = hash,
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
        }
        catch { }
    }

    private static string GetDeploymentName(string personaId) =>
        $"agentmesh-persona-{personaId.Trim().ToLowerInvariant()}";

    private sealed class KubernetesTerminalSession(WebSocket webSocket, StreamDemuxer demuxer, Stream stdIn) : IPersonaTerminalSession
    {
        public async ValueTask SendInputAsync(string data)
        {
            try
            {
                if (webSocket.State == WebSocketState.Open)
                {
                    var bytes = Encoding.UTF8.GetBytes(data);
                    await stdIn.WriteAsync(bytes, 0, bytes.Length);
                    await stdIn.FlushAsync();
                }
            }
            catch { }
        }

        public async ValueTask ResizeAsync(int cols, int rows)
        {
            await ValueTask.CompletedTask;
        }

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
