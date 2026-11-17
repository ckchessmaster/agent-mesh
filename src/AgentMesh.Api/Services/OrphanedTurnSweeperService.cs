using AgentMesh.Api.Hubs;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Services;

/// <summary>
/// Settles sessions that are still claiming to be worked on after every owner that could work on them
/// has stopped.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a sweeper is needed when the turn runner already has a ceiling.</strong> The runner's
/// ceiling only fires for a turn a workflow is watching. A turn dispatched outside a workflow — the
/// cockpit's chat path, a handoff, a retry — has no observer at all, and neither does a turn whose
/// worker pod was deleted while it was polling. The result is the failure this service exists to erase:
/// a session row that says <c>thinking</c> forever, a cockpit that says "running a command" forever, and
/// an operator who has to be told the agent is not thinking, it is gone.
/// </para>
/// <para>
/// <strong>Why age is the test.</strong> Proving an owner is dead needs a lease or a heartbeat per
/// session, which the platform does not have and which would have to be threaded through the CLI, the
/// API and the worker in one change. Age is a weaker test that is safe because of the ordering encoded in
/// <see cref="TurnStallPolicy.OrphanedTurnAfter"/>: it sits above the runner's own ceiling, so a session
/// this service touches is one no observer will ever conclude, and a live agent's next event overwrites
/// the status anyway. Weaker is not the same as wrong.
/// </para>
/// <para>
/// <strong>Why it runs in the API.</strong> The API owns <c>AgentMeshHub</c>. Settling without telling
/// connected cockpits leaves the indicator up until a refresh, which is the exact symptom being fixed —
/// it would be correct in the database and wrong on the screen.
/// </para>
/// </remarks>
public class OrphanedTurnSweeperService(
    IServiceProvider serviceProvider,
    IHubContext<AgentMeshHub>? hubContext,
    IConfiguration config,
    ILogger<OrphanedTurnSweeperService> logger) : BackgroundService
{
    /// <summary>Author recorded on a sweep settle, distinguishable from an operator's manual recovery.</summary>
    public const string SweeperAgentId = "platform-sweeper";

    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(
        Math.Max(30, config.GetValue("TurnReconciler:CheckIntervalSeconds", 300)));

    /// <summary>
    /// How long a live session may stay quiet before it is swept.
    /// </summary>
    /// <remarks>
    /// Defaults to the policy ladder's floor and can only be raised by configuration. Lowering it below
    /// <see cref="TurnStallPolicy.OrphanedTurnAfter"/> would let the sweeper settle turns the worker is
    /// still observing, racing it to write a false status over a live agent, so a smaller configured
    /// value is ignored rather than honoured.
    /// </remarks>
    private readonly TimeSpan _orphanAfter = MaxOrPolicy(
        config.GetValue("TurnReconciler:OrphanMinutes", (int)TurnStallPolicy.OrphanedTurnAfter.TotalMinutes));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "OrphanedTurnSweeperService started (orphan threshold: {Threshold}, interval: {Interval})",
            _orphanAfter, _checkInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "OrphanedTurnSweeperService cycle failed");
            }

            try
            {
                await Task.Delay(_checkInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One sweep: settle by age, then announce what moved.
    /// </summary>
    /// <remarks>
    /// Announcements happen after <c>SaveChanges</c> and never inside the transaction's failure path. A
    /// broadcast of a settle that was rolled back would put the cockpit in a state the database disagrees
    /// with, which is the class of bug this service is meant to remove rather than add to.
    /// </remarks>
    internal async Task<int> SweepAsync(CancellationToken ct)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentMeshDbContext>();

        var cutoff = DateTimeOffset.UtcNow - _orphanAfter;
        var settled = await TurnStallSettler.SettleOrphansAsync(
            db, cutoff, BuildNotice(), SweeperAgentId, filter: null, ct);

        if (settled.Count == 0) return 0;

        logger.LogWarning(
            "Settled {Count} orphaned turn session(s) quieter than {Threshold}: {SessionIds}",
            settled.Count, _orphanAfter, string.Join(", ", settled.Select(s => s.Id.ToString("N"))));

        // The job has to say the same thing the session now says, or a swept turn leaves the cockpit
        // showing an active job with no recovery affordance and no hint that anything is wrong.
        foreach (var session in settled)
        {
            if (TurnStallSettler.TryGetJobIdFromMetadata(session.MetadataJson, out var jobId))
            {
                await TurnStallSettler.MarkJobStalledAsync(db, jobId, BuildNotice(), ct);
            }

            if (hubContext is not null)
            {
                await hubContext.Clients.All.SendAsync("SessionUpdated", session, ct);
            }
        }

        return settled.Count;
    }

    private string BuildNotice() =>
        $"No transcript event for {(int)_orphanAfter.TotalMinutes} minutes, so no session owner was " +
        "advancing this turn. The platform settled it so the cockpit stops reporting work that is not " +
        "running. If the agent is in fact still alive, its next event reopens the session automatically.";

    /// <summary>Floors a configured threshold at the policy value; see <see cref="_orphanAfter"/>.</summary>
    private static TimeSpan MaxOrPolicy(int configuredMinutes)
    {
        if (configuredMinutes <= 0) return TurnStallPolicy.OrphanedTurnAfter;
        var requested = TimeSpan.FromMinutes(configuredMinutes);
        return requested > TurnStallPolicy.OrphanedTurnAfter ? requested : TurnStallPolicy.OrphanedTurnAfter;
    }
}
