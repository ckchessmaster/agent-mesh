namespace AgentMesh.Core.Services;

public record PersonaEnvironmentStatus(
    string PersonaId,
    string Status, // "stopped", "running", "starting", "error"
    string Runtime, // "docker", "podman", "kubernetes", "none"
    string? ContainerId = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? LastActivityAt = null,
    long? MemoryUsageBytes = null,
    string? ErrorMessage = null);

public interface IPersonaContainerManager
{
    string RuntimeName { get; }
    Task<PersonaEnvironmentStatus> GetStatusAsync(string personaId, CancellationToken ct = default);
    Task EnsureRunningAsync(string personaId, CancellationToken ct = default);
    Task StopAsync(string personaId, CancellationToken ct = default);
    Task<string> GetLogsAsync(string personaId, int tailLines = 100, CancellationToken ct = default);
}
