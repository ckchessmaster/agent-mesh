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

        try
        {
            var deployment = await _client.AppsV1.ReadNamespacedDeploymentAsync(deploymentName, _namespace, cancellationToken: ct);
            var replicas = deployment.Spec.Replicas ?? 0;
            var readyReplicas = deployment.Status.ReadyReplicas ?? 0;

            var status = readyReplicas > 0 ? "running" : (replicas > 0 ? "starting" : "stopped");

            return new PersonaEnvironmentStatus(
                PersonaId: personaId,
                Status: status,
                Runtime: RuntimeName,
                ContainerId: deploymentName,
                StartedAt: deployment.Metadata.CreationTimestamp,
                LastActivityAt: DateTimeOffset.UtcNow);
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

        try
        {
            var deployment = await _client.AppsV1.ReadNamespacedDeploymentAsync(deploymentName, _namespace, cancellationToken: ct);
            if ((deployment.Spec.Replicas ?? 0) == 0)
            {
                _logger.LogInformation("Scaling up existing deployment {Name} for @{PersonaId} to 1 replica", deploymentName, personaId);
                var patchJson = JsonSerializer.Serialize(new { spec = new { replicas = 1 } });
                var patch = new V1Patch(patchJson, V1Patch.PatchType.MergePatch);
                await _client.AppsV1.PatchNamespacedDeploymentAsync(patch, deploymentName, _namespace, cancellationToken: ct);
            }
            return;
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Create deployment
        }

        _logger.LogInformation("Creating new deployment {Name} for @{PersonaId} in namespace {Namespace}", deploymentName, personaId, _namespace);

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
                        Containers = new List<V1Container>
                        {
                            new()
                            {
                                Name = "persona-runner",
                                Image = _defaultImage,
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

            var pod = pods.Items[0];
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
