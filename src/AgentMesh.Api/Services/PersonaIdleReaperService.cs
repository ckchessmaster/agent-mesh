using AgentMesh.Api.Hubs;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Services;

public class PersonaIdleReaperService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IPersonaContainerManager _containerManager;
    private readonly ILogger<PersonaIdleReaperService> _logger;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _checkInterval;

    public PersonaIdleReaperService(
        IServiceProvider serviceProvider,
        IPersonaContainerManager containerManager,
        IConfiguration config,
        ILogger<PersonaIdleReaperService> logger)
    {
        _serviceProvider = serviceProvider;
        _containerManager = containerManager;
        _logger = logger;

        var timeoutMinutes = config.GetValue<int>("PersonaContainer:IdleTimeoutMinutes", 30);
        _idleTimeout = TimeSpan.FromMinutes(Math.Max(1, timeoutMinutes));

        var checkSeconds = config.GetValue<int>("PersonaContainer:ReaperCheckIntervalSeconds", 60);
        _checkInterval = TimeSpan.FromSeconds(Math.Max(5, checkSeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PersonaIdleReaperService started (Timeout: {Timeout}, Interval: {Interval})",
            _idleTimeout, _checkInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_checkInterval, stoppingToken);
                await ReapIdlePersonasAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Error in PersonaIdleReaperService cycle: {Error}", ex.Message);
            }
        }
    }

    private async Task ReapIdlePersonasAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentMeshDbContext>();

        var personas = await db.Personas
            .AsNoTracking()
            .Select(p => p.Id)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;

        foreach (var personaId in personas)
        {
            try
            {
                var status = await _containerManager.GetStatusAsync(personaId, ct);
                if (status.Status != "running")
                {
                    continue;
                }

                // Check if any active thinking session exists for this persona
                var hasActiveThinking = await db.AgentSessions
                    .AsNoTracking()
                    .AnyAsync(s => s.PersonaId == personaId && s.Status == "thinking", ct);

                if (hasActiveThinking)
                {
                    continue;
                }

                // Find last activity time across sessions for this persona
                var lastSessionActivity = await db.AgentSessions
                    .AsNoTracking()
                    .Where(s => s.PersonaId == personaId)
                    .OrderByDescending(s => s.UpdatedAt)
                    .Select(s => (DateTimeOffset?)s.UpdatedAt)
                    .FirstOrDefaultAsync(ct);

                // Determine the most recent activity timestamp across container start, container ping, and session updates.
                // Never use null-coalescing here because an older session timestamp must not override a recently started container!
                var candidateTimes = new[] { status.StartedAt, status.LastActivityAt, lastSessionActivity };
                var activeTimestamps = candidateTimes.Where(t => t.HasValue).Select(t => t!.Value).ToList();
                var lastActivity = activeTimestamps.Count > 0 ? activeTimestamps.Max() : (DateTimeOffset?)null;

                // Safety grace period: if the container started recently within the idle timeout window, do not reap
                if (status.StartedAt.HasValue && (now - status.StartedAt.Value) < _idleTimeout)
                {
                    continue;
                }

                if (lastActivity.HasValue && now - lastActivity.Value > _idleTimeout)
                {
                    _logger.LogInformation("Persona @{PersonaId} idle for {Duration}. Stopping container environment.",
                        personaId, now - lastActivity.Value);

                    await _containerManager.StopAsync(personaId, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed checking idle status for persona @{PersonaId}: {Error}", personaId, ex.Message);
            }
        }
    }
}
