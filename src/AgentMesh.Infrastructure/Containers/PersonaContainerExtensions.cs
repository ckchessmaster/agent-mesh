using AgentMesh.Core.Services;
using k8s;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMesh.Infrastructure.Containers;

public static class PersonaContainerExtensions
{
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
