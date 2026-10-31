using AgentMesh.Core.Repositories;
using AgentMesh.Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentMesh.Infrastructure;

/// <summary>
/// DI wiring for the Infrastructure repository layer.
/// </summary>
public static class AgentMeshRepositoryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the repositories that live in Infrastructure against the Core contracts.
    /// </summary>
    /// <remarks>
    /// Scoped, not singleton: <see cref="AgentMemoryRepository"/> holds the per-request
    /// <c>AgentMeshDbContext</c>, and the upsert path depends on that context's change tracker being
    /// isolated to one request. <c>TryAddScoped</c> lets a test host substitute a fake by registering
    /// first, which is how the MCP-level tests drive the tool without a database.
    /// </remarks>
    public static IServiceCollection AddAgentMeshRepositories(this IServiceCollection services)
    {
        services.TryAddScoped<IAgentMemoryRepository, AgentMemoryRepository>();
        services.TryAddScoped<IWorkspaceStateRepository, WorkspaceStateRepository>();
        return services;
    }
}
