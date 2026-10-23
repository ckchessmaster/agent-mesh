using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgentMesh.Api.Services;

/// <summary>
/// Releases handoffs whose grace window has elapsed.
///
/// The alternative to sweeping is a durable timer per handoff, but that only pays for itself if we
/// already depend on Temporal for job orchestration — and we do not for these turns. A sweep is
/// idempotent, survives a restart with no state to recover, and the worst case is one extra
/// <see cref="JobHandoffOptions.SweepInterval"/> of latency on a path that already waits minutes.
///
/// Multi-instance safe: each replica races on the same rows, but the status flip in
/// <see cref="IJobHandoffService.ExpirePendingAsync"/> is guarded by a Pending check, so only one
/// replica can move a given handoff out of Pending.
/// </summary>
public class JobHandoffReaper(
    IServiceScopeFactory scopeFactory,
    IOptions<JobHandoffOptions> options,
    ILogger<JobHandoffReaper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.SweepInterval;
        logger.LogInformation("Job handoff reaper started (sweep every {Interval}s)", interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);

                using var scope = scopeFactory.CreateScope();
                var handoffs = scope.ServiceProvider.GetRequiredService<IJobHandoffService>();
                var db = scope.ServiceProvider.GetRequiredService<AgentMeshDbContext>();

                var due = await db.JobHandoffs.AsNoTracking()
                    .CountAsync(h => h.Status == JobHandoffStatus.Pending
                                     && h.ReleaseDeadlineAt <= DateTimeOffset.UtcNow, stoppingToken);
                if (due == 0) continue;

                var released = await handoffs.ExpirePendingAsync(stoppingToken);
                if (released > 0)
                {
                    logger.LogInformation("Released {Count} handoff(s) whose grace window expired", released);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let a transient DB blip kill the sweep; a dead reaper means stuck jobs.
                logger.LogError(ex, "Handoff sweep failed; retrying next tick");
            }
        }

        logger.LogInformation("Job handoff reaper stopped");
    }
}
