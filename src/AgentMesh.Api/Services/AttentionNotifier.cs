using AgentMesh.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace AgentMesh.Api.Services;

/// <summary>
/// The SignalR event names the notification surface subscribes to.
/// </summary>
/// <remarks>
/// Frozen strings, for the same reason <see cref="AttentionTypes"/> is: the Cockpit's notification
/// seen-set and its event subscriptions are persisted in the browser, so renaming an event orphans
/// every already-open tab. <c>AttentionBroadcastTests</c> pins both names.
/// </remarks>
public static class AttentionEvents
{
    /// <summary>A job started waiting on a human. Payload is an <see cref="AttentionItemDto"/>.</summary>
    public const string Needed = "AttentionNeeded";

    /// <summary>
    /// A job is no longer waiting on a human. Payload is an <see cref="AttentionResolvedDto"/>.
    /// </summary>
    public const string Resolved = "AttentionResolved";
}

/// <summary>
/// The body of <see cref="AttentionEvents.Resolved"/>.
/// </summary>
/// <param name="JobId">
/// The job that left the inbox. This alone is enough for a client to act: dismiss every notification
/// and seen-set entry whose key starts with <c>"{jobId}:"</c>.
/// </param>
/// <param name="ItemId">
/// Always null in the current design. The server keeps no record of what it last announced (see
/// <see cref="AttentionNotifier"/>), so it cannot name the wait that ended; a client that wants to
/// close one specific OS notification matches on <see cref="JobId"/> instead. The field exists so that
/// adding a precise id later is additive rather than a payload-shape change.
/// </param>
/// <param name="Type">Reserved for the same reason as <paramref name="ItemId"/>.</param>
/// <param name="ResolvedAt">When the server observed the wait gone — not when it started.</param>
public sealed record AttentionResolvedDto(
    string JobId,
    string? ItemId,
    string? Type,
    DateTimeOffset ResolvedAt);

/// <summary>
/// Pushes one job's pending-attention state to every connected Cockpit.
/// </summary>
/// <remarks>
/// Implementations must never throw: this runs after a durable write has committed, and failing to
/// tell a browser about a wait is not worth turning a successful POST into a 500.
/// </remarks>
public interface IAttentionNotifier
{
    /// <summary>
    /// Recomputes one job's attention item and broadcasts it, or broadcasts a resolution when the job
    /// is no longer waiting. A <paramref name="jobId"/> that is not a job id is ignored.
    /// </summary>
    Task RefreshAsync(string? jobId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns "something just changed for job X" into a global SignalR event.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a push when the rollup is already pollable.</strong> <c>GET /api/attention</c> answers
/// "what is waiting now", which is the right shape for the inbox list but a poor one for a desktop
/// banner: the banner has to fire within a second of an agent parking, and polling a cross-project
/// read every few seconds to approximate that is both slower and more expensive than the event. The
/// rollup stays the source of truth — a client that missed an event (closed tab, dropped socket, a
/// restart between the write and the read) converges on the next poll. Nothing here is the only copy
/// of anything.
/// </para>
/// <para>
/// <strong>Why <c>Clients.All</c> and not the job group.</strong> The bell is cross-project, and a
/// cockpit tab only joins the group of the job it is looking at (<c>AgentMeshHub.JoinJob</c>). Group
/// delivery would therefore reach exactly the operator who is already staring at the blocked job and
/// nobody else — the "global" in the requirement is the whole point. This matches how
/// <c>ProgressHeartbeatUpdated</c> and <c>JobUpdated</c> are already sent.
/// </para>
/// <para>
/// <strong>Why it recomputes instead of classifying the write.</strong> The notifier does not look at
/// what the write site changed; it asks <see cref="IAttentionQueryService.QueryJobAsync"/> what the
/// job's state is now, using the same predicates and the same fold that serve the endpoint. A
/// classifier that reads the delta ("a card was created, so raise planApproval") is a second
/// implementation of the rule, and the two drift: the write sites would start announcing waits the
/// inbox does not contain, which is the failure mode that makes a bell untrustworthy. Recomputing
/// costs a handful of indexed reads on a write path and cannot disagree with the endpoint by
/// construction.
/// </para>
/// <para>
/// <strong>Why it is stateless.</strong> The obvious optimisation — remember the last item announced
/// per job and only emit on change — would let the server suppress duplicate <c>AttentionNeeded</c>
/// events and name the id that a <c>AttentionResolved</c> closes. It is deliberately not done. That
/// cache is per-process: SignalR here has no backplane, so a second API replica or a restart would
/// silently swallow the one event a client was waiting for, replacing a duplicate-noise problem (which
/// the client already solves by deduplicating on <see cref="AttentionItemDto.Id"/>) with a
/// lost-notification problem. So every refresh emits exactly one event, duplicates are expected, and
/// they are idempotent: the same wait always produces a byte-identical item id.
/// </para>
/// <para>
/// <strong>Resolution is derived, never written.</strong> There is no "resolve" call anywhere: a job
/// that stopped waiting (approved, answered, resumed, completed, archived) simply computes to no item,
/// and the client clears by <c>jobId</c>. That is the same property that lets the rollup need no new
/// columns and no dismiss endpoint.
/// </para>
/// </remarks>
public sealed class AttentionNotifier(
    IAttentionQueryService attention,
    IHubContext<AgentMeshHub> hubContext,
    ILogger<AttentionNotifier> logger) : IAttentionNotifier
{
    public async Task RefreshAsync(string? jobId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(jobId?.Trim(), out var parsed))
        {
            // Write sites pass ids that are not always job Guids (an agent job keyed by a string, a
            // malformed route value). Nothing to announce, and nothing to resolve either.
            return;
        }

        try
        {
            var item = await attention.QueryJobAsync(parsed, cancellationToken);
            if (item is not null)
            {
                await hubContext.Clients.All.SendAsync(AttentionEvents.Needed, item, cancellationToken);
                logger.LogDebug("Attention needed for job {JobId}: {ItemId}", parsed, item.Id);
                return;
            }

            await hubContext.Clients.All.SendAsync(
                AttentionEvents.Resolved,
                new AttentionResolvedDto(parsed.ToString(), null, null, DateTimeOffset.UtcNow),
                cancellationToken);

            logger.LogDebug("Attention resolved for job {JobId}", parsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A dead hub or a hiccup on the rollup read must not fail the write that just committed.
            // The next write to this job, or the client's next poll, restates the state.
            logger.LogWarning(ex, "Attention broadcast skipped for job {JobId}", parsed);
        }
        catch (OperationCanceledException)
        {
            // The caller went away mid-broadcast. Same best-effort reasoning; nothing to retry, and
            // rethrowing would surface a cancellation as a failed write.
            logger.LogDebug("Attention broadcast cancelled for job {JobId}", parsed);
        }
    }
}

/// <summary>
/// Terse, null-tolerant call form for the write sites.
/// </summary>
/// <remarks>
/// Every class below takes the notifier as an <em>optional</em> constructor parameter, because each is
/// constructed by dozens of existing tests that do not exercise notifications and the repo's precedent
/// (<c>IHubContext</c>, <c>IWorkflowSignalRouter</c>) is to keep such collaborators optional with a
/// production-container pin rather than to rewrite them. That leaves the null case, and a three-line
/// guard at fifteen call sites is worse than one here: an extension method on a null receiver is legal
/// precisely because it is not a virtual dispatch, so "not wired in this host" costs nothing.
/// <see cref="ApiServiceRegistrationTests"/> pins that the real container does wire it.
/// </remarks>
public static class AttentionNotifierExtensions
{
    /// <summary>Refreshes attention for <paramref name="jobId"/> when a notifier is wired in.</summary>
    public static Task RefreshAttentionAsync(
        this IAttentionNotifier? notifier, Guid jobId, CancellationToken cancellationToken = default)
        => notifier?.RefreshAsync(jobId.ToString(), cancellationToken) ?? Task.CompletedTask;

    /// <summary>Refreshes attention for <paramref name="jobId"/> when a notifier is wired in.</summary>
    public static Task RefreshAttentionAsync(
        this IAttentionNotifier? notifier, string? jobId, CancellationToken cancellationToken = default)
        => notifier?.RefreshAsync(jobId, cancellationToken) ?? Task.CompletedTask;
}
