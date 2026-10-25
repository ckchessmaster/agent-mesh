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

public record PersonaDriverVerificationStep(
    string Name,
    string Status, // "pending", "success", "failed", "warning"
    string Details);

public record PersonaDriverVerificationResult(
    string PersonaId,
    string DriverId,
    string OverallStatus, // "passed", "failed", "warning"
    List<PersonaDriverVerificationStep> Steps,
    string? ErrorMessage = null);

public record PersonaAuthStartResult(
    string PersonaId,
    string DriverId,
    string Status, // "ready", "waiting_code", "already_authenticated", "error"
    string? AuthUrl = null,
    string? Instructions = null,
    string? ErrorMessage = null);

public record PersonaAuthSubmitResult(
    string PersonaId,
    string Status, // "authenticated", "failed"
    string? Message = null,
    string? ErrorMessage = null);

public interface IPersonaTerminalSession : IAsyncDisposable
{
    ValueTask SendInputAsync(string data);
    ValueTask ResizeAsync(int cols, int rows);
}

public interface IPersonaContainerManager
{
    string RuntimeName { get; }
    Task<PersonaEnvironmentStatus> GetStatusAsync(string personaId, CancellationToken ct = default);
    Task EnsureRunningAsync(string personaId, CancellationToken ct = default);
    Task StopAsync(string personaId, CancellationToken ct = default);
    Task<string> GetLogsAsync(string personaId, int tailLines = 100, CancellationToken ct = default);
    Task<PersonaDriverVerificationResult> VerifyDriverAsync(string personaId, CancellationToken ct = default);
    Task<PersonaAuthStartResult> StartAuthAsync(string personaId, CancellationToken ct = default);
    Task<PersonaAuthSubmitResult> SubmitAuthCodeAsync(string personaId, string code, CancellationToken ct = default);
    Task<PersonaAuthSubmitResult> ConfigureApiKeyAsync(string personaId, string apiKey, string? provider = null, CancellationToken ct = default);
    Task<(int ExitCode, string StdOut, string StdErr)> ExecInContainerAsync(string personaId, string command, CancellationToken ct = default);
    Task<IPersonaTerminalSession> AttachTerminalAsync(
        string personaId,
        int cols,
        int rows,
        Func<string, Task> onOutput,
        Func<int, Task> onClosed,
        CancellationToken ct = default);
}
