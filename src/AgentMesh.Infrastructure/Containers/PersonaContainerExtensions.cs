using AgentMesh.Core.Services;
using k8s;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMesh.Infrastructure.Containers;

public static class PersonaContainerExtensions
{
    public const string SyncPersonaStateCommand =
        "sync_persona_state 2>/dev/null || (" +
        "mkdir -p /workspace/.agentmesh/state/.config /workspace/.agentmesh/state/.aip /workspace/.agentmesh/state/.gemini /workspace/.agentmesh/state/.claude && " +
        "cp -a /root/.config/. /workspace/.agentmesh/state/.config/ 2>/dev/null && " +
        "cp -a /root/.aip/. /workspace/.agentmesh/state/.aip/ 2>/dev/null && " +
        "cp -a /root/.gemini/. /workspace/.agentmesh/state/.gemini/ 2>/dev/null && " +
        "cp -a /root/.claude/. /workspace/.agentmesh/state/.claude/ 2>/dev/null)";

    public static IServiceCollection AddPersonaContainerManager(
        this IServiceCollection services,
        IConfiguration config)
    {
        var runtimeSetting = config["PersonaContainer:Runtime"]?.ToLowerInvariant();

        if (runtimeSetting == "kubernetes" || (string.IsNullOrWhiteSpace(runtimeSetting) && KubernetesClientConfiguration.IsInCluster()))
        {
            services.AddSingleton<IPersonaContainerManager, KubernetesPersonaContainerManager>();
        }
        else
        {
            services.AddSingleton<IPersonaContainerManager, DockerPersonaContainerManager>();
        }

        return services;
    }
}
