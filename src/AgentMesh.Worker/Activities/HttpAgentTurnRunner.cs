using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMesh.Core.Models;
using AgentMesh.Core.Repositories;
using AgentMesh.Core.Services;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using Temporalio.Activities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentMesh.Worker.Activities;

/// <summary>
/// Production <see cref="IAgentTurnRunner"/>: dispatches the assembled prompt through the platform's
/// own turn path, then observes the turn to its conclusion and derives a
/// <see cref="TurnOutcome"/> from what the agent actually did.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why dispatch is HTTP and observation is SQL.</strong> Starting a turn correctly is the
/// exclusive job of the API's <c>JobTurnDispatcher</c>: it mints the turn session, wakes the persona
/// container, resolves the driver, takes the workspace lease and broadcasts <c>PromptReceived</c>. The
/// worker cannot call it (the dispatcher lives in the API assembly, and referencing the API from the
/// worker would invert the dependency graph), so dispatch goes over HTTP to the one component that
/// owns it. Observation is the reverse: turn state is rows in tables the worker already has a
/// connection to, so reading them directly is one indexed query instead of a second network hop that
/// fails during the exact window when the API is being rolled out.
/// </para>
/// <para>
/// <strong>Why polling and not a callback.</strong> A push from the API to the worker on turn
/// completion would need either a second Temporal connection (the API signalling the workflow
/// directly) or a message bus. The first is the right answer for <em>human</em> events — chat replies
/// and reviews, which is exactly what the workflow's signals are for — but a turn-completion push is a
/// machine event with no human in the loop, and building it would mean the workflow's progress depends
/// on the API knowing how to reach Temporal. Polling a bounded query keeps the worker's liveness
/// independent of the API's, at the cost of up to one poll interval of latency on a turn that takes
/// minutes. That is the correct trade: 2 seconds of latency on a 10-minute turn is nothing, and a
/// missed push that strands a workflow forever is everything.
/// </para>
/// <para>
/// <strong>Why the outcome is derived from recorded tool calls.</strong> The agent's final prose is
/// not a control-flow signal: a model that says "I have opened the PR and am awaiting review" without
/// having called <c>open_pull_request</c> is describing an intention, and halting the workflow to wait
/// for a review of a PR that does not exist would hang the task indefinitely. The durable record of
/// what happened is the <c>tool_call</c> row the MCP layer wrote, and the job-ledger row the tool
/// itself inserted. Those are measurements; the prose is a claim.
/// </para>
/// </remarks>
public class HttpAgentTurnRunner : IAgentTurnRunner
{
    private readonly AgentMeshDbContext db;
    private readonly IWorkspaceStateRepository checkpoints;
    private readonly HttpClient httpClient;
    private readonly IConfiguration config;
    private readonly ILogger<HttpAgentTurnRunner> logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public HttpAgentTurnRunner(
        AgentMeshDbContext db,
        IWorkspaceStateRepository checkpoints,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<HttpAgentTurnRunner> logger)
    {
        this.db = db;
        this.checkpoints = checkpoints;
        this.config = config;
        this.logger = logger;
        httpClient = httpClientFactory.CreateClient(HttpClientName);
    }

    /// <summary>Named client so timeouts are configured once, in the worker's DI setup.</summary>
    public const string HttpClientName = "agentmesh-macro-turn";

    /// <summary>Configuration key for the API base URL the worker dispatches through.</summary>
    public const string ApiBaseUrlConfigKey = "Platform:ApiBaseUrl";

    /// <summary>Configuration key for the key sent in <c>X-Api-Key</c>.</summary>
    public const string ApiKeyConfigKey = "Platform:ApiKey";

    /// <summary>
    /// Interval between turn-state polls.
    /// </summary>
    /// <remarks>
    /// 2 seconds balances two costs. Shorter means a query per turn per second against a table that
    /// grows with every session; longer means the workflow sits idle after the agent already finished,
    /// and idle workflow time is what this job is trying to eliminate. Against a turn measured in
    /// minutes, 2 seconds is noise.
    /// </remarks>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Ceiling on one observed turn.
    /// </summary>
    /// <remarks>
    /// Must sit inside the workflow's 2-hour <c>StartToCloseTimeout</c> with room to spare, because the
    /// activity is what does the waiting. It is not a substitute for that timeout: this one produces a
    /// clean <see cref="TurnOutcomeStatus.StuckLoopDetected"/> with a message naming the session to
    /// inspect, whereas the activity timeout produces a Temporal timeout and a retry. Reaching this
    /// limit means the runner is wedged, which is worth reporting as a stuck turn rather than as a
    /// platform fault.
    /// </remarks>
    public static readonly TimeSpan MaxTurnDuration = TimeSpan.FromMinutes(110);

    /// <summary>
    /// How long a session may sit in a terminal status before the turn is considered over.
    /// </summary>
    /// <remarks>
    /// A grace window rather than an immediate read, because the runner writes the final
    /// <c>response</c> message and the status change in separate requests. Reading the status the
    /// instant it flips can miss the response row that landed microseconds earlier, and the response
    /// is one of the inputs to the outcome.
    /// </remarks>
    public static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(5);

    /// <summary>Statuses that mean the runner is not working on this turn any more.</summary>
    /// <remarks>
    /// <c>waiting_for_input</c> is in this set because it is how the platform parks a session when
    /// <c>post_job_message(requires_reply: true)</c> parks it — the turn is over and a human owes an
    /// answer, which is exactly <see cref="TurnOutcomeStatus.AwaitingInput"/>.
    /// </remarks>
    private static readonly string[] TerminalStatuses =
        ["idle", "completed", "stopped", "error", "archived", "waiting_for_input"];

    /// <summary>
    /// Runs (or re-attaches to) one turn.
    /// </summary>
    public async Task<TurnOutcome> RunTurnAsync(AgentTurnRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var input = request.Input;

        // ── Re-attach first ──────────────────────────────────────────────────────
        // The pairing is persisted before the dispatch is confirmed, and the dispatch itself is
        // idempotent on the session id, so a retry of a timed-out attempt re-attaches to the agent
        // that is already running instead of starting a second one on the same branch.
        var sessionId = await ResolveExistingSessionAsync(request, ct);

        if (sessionId is null)
        {
            sessionId = await DispatchAsync(request, ct);
        }

        if (sessionId is null)
        {
            // Dispatch failed in a way that produced no session. Throwing (rather than returning
            // Failed) is what makes Temporal retry this: an API that was mid-restart is not a task
            // failure, and a task that halts because the control plane was briefly down is a bug in
            // the platform, not in the agent.
            throw new InvalidOperationException(
                $"Could not dispatch a coding turn for task {input.TaskId} (job {input.JobId}, " +
                $"persona {input.PersonaId ?? "(none)"}): the API returned no turn session.");
        }

        logger.LogInformation(
            "Macro turn observing session {SessionId} for job {JobId} task {TaskId}",
            sessionId, input.JobId, input.TaskId);

        // The floor for the time-windowed ledger fallback, captured once before any waiting. See
        // ResolveLedgerFloorAsync for why this has to be a durable turn boundary rather than "now".
        var ledgerFloor = await ResolveLedgerFloorAsync(input, sessionId.Value, ct);

        return await ObserveAsync(input, sessionId.Value, ledgerFloor, ct);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Dispatch
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The session this task is already running in, if any.
    /// </summary>
    /// <remarks>
    /// Read from the checkpoint rather than from the workflow input, because the input is fixed at
    /// workflow start and cannot know about a session created by turn three. The checkpoint is written
    /// by the turn activity at every boundary, so it is the freshest record of "which session is this
    /// task's". Only a session that is still live is re-attached to; a completed session is history and
    /// reusing it would skip dispatch, drop prompts, and observe a stale terminal state.
    /// </remarks>
    internal async Task<Guid?> ResolveExistingSessionAsync(AgentTurnRequest request, CancellationToken ct)
    {
        try
        {
            var checkpoint = await checkpoints.GetCheckpointAsync(
                request.Input.JobId, request.Input.TaskId, ct);

            if (Guid.TryParse(checkpoint?.TurnSessionId?.Trim(), out var fromCheckpoint) &&
                fromCheckpoint != Guid.Empty)
            {
                // Only re-attach to a session that is still live. A completed one is history: reusing
                // its id would append this turn's messages to a closed transcript and the settle check
                // below would immediately read it as finished, returning an outcome derived from the
                // *previous* turn.
                var live = await db.AgentSessions.AsNoTracking()
                    .Where(s => s.Id == fromCheckpoint)
                    .Select(s => s.Status)
                    .FirstOrDefaultAsync(ct);

                if (live is not null && !TerminalStatuses.Contains(live, StringComparer.OrdinalIgnoreCase))
                {
                    logger.LogInformation(
                        "Re-attaching to live turn session {SessionId} (status {Status}) from checkpoint for task {TaskId} " +
                        "instead of dispatching a second agent",
                        fromCheckpoint, live, request.Input.TaskId);
                    return fromCheckpoint;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not check checkpoint for an existing turn session for task {TaskId}",
                request.Input.TaskId);
        }

        // An explicit id on the request: the workflow was started with it, or an activity attempt passed it.
        // We must apply the same liveness check: an already-terminal explicit session must not skip dispatch.
        if (Guid.TryParse(request.TurnSessionId?.Trim(), out var explicitId) && explicitId != Guid.Empty)
        {
            try
            {
                var live = await db.AgentSessions.AsNoTracking()
                    .Where(s => s.Id == explicitId)
                    .Select(s => s.Status)
                    .FirstOrDefaultAsync(ct);

                if (live is not null && !TerminalStatuses.Contains(live, StringComparer.OrdinalIgnoreCase))
                {
                    logger.LogInformation(
                        "Re-attaching to live explicit turn session {SessionId} (status {Status}) for task {TaskId} " +
                        "instead of dispatching a second agent",
                        explicitId, live, request.Input.TaskId);
                    return explicitId;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Could not check liveness of explicit turn session {SessionId} for task {TaskId}",
                    explicitId, request.Input.TaskId);
            }
        }

        return null;
    }

    private async Task<Guid?> DispatchAsync(AgentTurnRequest request, CancellationToken ct)
    {
        var baseUrl = config[ApiBaseUrlConfigKey];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException(
                $"'{ApiBaseUrlConfigKey}' is not configured on the worker, so a coding turn cannot be " +
                "dispatched. The worker holds no LLM client by design: turns run in a persona " +
                "container, started by the API's turn dispatcher over this endpoint.");
        }

        var payload = new
        {
            prompt = request.Prompt,
            personaId = request.PersonaId,
            taskId = request.Input.TaskId,
            turnSessionId = (Guid?)null,
            sender = "temporal-worker",
        };

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl.TrimEnd('/')}/api/project-jobs/{request.Input.JobId}/macro-turns")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json"),
        };

        var apiKey = config[ApiKeyConfigKey];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            message.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        }

        using var response = await httpClient.SendAsync(message, ct);
        var body = await ReadBodyAsync(response, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // The endpoint refused because the named session belongs to another job. That is a
            // misconfiguration or a corrupt checkpoint, not a transient fault, and retrying would loop
            // on the same refusal — so it is reported as a halt the workflow can surface.
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Macro turn dispatch for task {request.Input.TaskId} failed with HTTP " +
                $"{((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}: " +
                $"{RedactTruncate(body)}.");
        }

        var parsed = TryReadGuidProperty(body, "turnSessionId");
        if (parsed is null)
        {
            throw new InvalidOperationException(
                $"Macro turn dispatch returned HTTP success but no turnSessionId: {RedactTruncate(body)}");
        }

        // Persist the pairing immediately, before any waiting. If this attempt is cancelled or the
        // worker dies mid-turn, the retry needs to find this session id — and this is the only place
        // it can be recorded, because the checkpoint at the end of the turn is written by an attempt
        // that may never happen.
        await RecordSessionPairingAsync(request, parsed.Value, ct);

        logger.LogInformation(
            "Dispatched macro turn session {SessionId} for job {JobId} task {TaskId} persona {PersonaId}",
            parsed, request.Input.JobId, request.Input.TaskId, request.PersonaId ?? "(none)");

        return parsed;
    }

    /// <summary>
    /// Writes the task-to-session pairing so a retry can re-attach.
    /// </summary>
    /// <remarks>
    /// A checkpoint write with the pre-turn facts plus the new session id. It is overwritten by the
    /// post-turn checkpoint a few minutes later; its only job is to survive long enough to be found by
    /// a retry, which is why it is written before the first poll rather than after the turn.
    /// </remarks>
    private async Task RecordSessionPairingAsync(AgentTurnRequest request, Guid sessionId, CancellationToken ct)
    {
        try
        {
            var existing = await checkpoints.GetCheckpointAsync(
                request.Input.JobId, request.Input.TaskId, ct);

            var checkpoint = (existing ?? TaskWorkingCheckpoint.Empty(request.Input.JobId, request.Input.TaskId))
                with
            {
                TurnSessionId = sessionId.ToString(),
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            await checkpoints.SaveCheckpointAsync(checkpoint, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The turn is dispatched and running; failing it now because the bookkeeping row could not
            // be written would discard real work. The cost of losing the pairing is that a retry
            // dispatches a second agent — bad, but recoverable — whereas losing the turn is not.
            logger.LogWarning(ex,
                "Could not record the turn session pairing for task {TaskId}; a retry of this activity " +
                "may dispatch a second agent", request.Input.TaskId);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Observation
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The earliest ledger row that can belong to this turn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fallback ledger window needs a lower bound, and the obvious candidates are both wrong.
    /// <c>session.CreatedAt</c> is wrong for a re-attached session: the session may be hours old, so
    /// the window would include the <c>requires_reply</c> row that <em>caused</em> the pause this turn
    /// is resuming from, the classifier would re-detect it, and the workflow would pause again on a
    /// message it already consumed — a livelock that looks like an agent that keeps asking the same
    /// question.
    /// </para>
    /// <para>
    /// <c>DateTimeOffset.UtcNow</c> at dispatch time is wrong in the other direction: on a re-attach we
    /// did not dispatch, so messages the agent posted between the original dispatch and this retry fall
    /// outside the window and are lost.
    /// </para>
    /// <para>
    /// The checkpoint's <c>UpdatedAt</c> is the one value that is correct in both cases, because it is
    /// written at every turn boundary: after turn N it is later than everything turn N posted, so turn
    /// N+1's window starts exactly where turn N's ended. It is also durable, so a retry of a crashed
    /// attempt computes the same floor the dead attempt would have.
    /// </para>
    /// <para>
    /// The session's own creation is kept as a floor for the first turn of a task, where there is no
    /// checkpoint to bound by.
    /// </para>
    /// </remarks>
    private async Task<DateTimeOffset> ResolveLedgerFloorAsync(
        TaskExecutionInput input,
        Guid sessionId,
        CancellationToken ct)
    {
        var sessionCreatedAt = DateTimeOffset.MinValue;
        try
        {
            sessionCreatedAt = await db.AgentSessions.AsNoTracking()
                .Where(s => s.Id == sessionId)
                .Select(s => s.CreatedAt)
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the creation time of session {SessionId}", sessionId);
        }

        try
        {
            var checkpoint = await checkpoints.GetCheckpointAsync(input.JobId, input.TaskId, ct);
            if (checkpoint is { IsEmpty: false })
            {
                return MaxOrMin(sessionCreatedAt, checkpoint.UpdatedAt);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Could not read the checkpoint floor for task {TaskId}; falling back to the session's creation time",
                input.TaskId);
        }

        return sessionCreatedAt == default ? DateTimeOffset.MinValue : sessionCreatedAt;
    }

    private static DateTimeOffset MaxOrMin(DateTimeOffset a, DateTimeOffset b) =>
        a == default ? b : (a > b ? a : b);

    private async Task<TurnOutcome> ObserveAsync(
        TaskExecutionInput input,
        Guid sessionId,
        DateTimeOffset ledgerFloor,
        CancellationToken ct)
    {
        if (!Guid.TryParse(input.JobId.Trim(), out var jobId))
        {
            return new TurnOutcome(TurnOutcomeStatus.Failed,
                $"Job id '{input.JobId}' is not a GUID; the turn cannot be observed.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        DateTimeOffset? terminalSince = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Heartbeat the activity so Temporal can tell "the agent is thinking" from "this worker
            // died holding the turn". Without it, a crashed worker leaves the activity running until
            // the 2-hour StartToClose elapses — two hours during which the task looks like it is
            // progressing while nothing is executing.
            Heartbeat($"observing session {sessionId} for task {input.TaskId}");

            var snapshot = await ReadTurnStateAsync(jobId, sessionId, ledgerFloor, ct);

            // Outcome signals are checked before the status, and on every iteration rather than only
            // at termination. A turn that opened a PR and then kept working (adding tests, replying to
            // a comment) has already produced a durable fact the workflow must not lose, and a turn
            // that parked on requires_reply is finished regardless of what the session status says a
            // moment later.
            var signal = ClassifySignals(snapshot);
            if (signal is not null)
            {
                // The scoping flag is logged rather than acted on. An inferred association can, in the
                // worst case, pause this workflow on another persona's unattributed question — which a
                // reply resolves — whereas suppressing a real requires_reply would hang the task with no
                // signal anyone can see. The asymmetry says log the weaker evidence and act on it.
                logger.LogInformation(
                    "Turn for task {TaskId} resolved to {Outcome} from recorded tool activity " +
                    "(session {SessionId}, ledger {LedgerScope})",
                    input.TaskId, signal.Status, sessionId,
                    snapshot.LedgerScopedToSession ? "session-scoped" : "time-windowed (inferred)");
                return signal;
            }

            if (TerminalStatuses.Contains(snapshot.SessionStatus, StringComparer.OrdinalIgnoreCase))
            {
                // Settle, then read once more: the final response row and the status flip are separate
                // writes and the response can land just after the status.
                terminalSince ??= DateTimeOffset.UtcNow;

                if (DateTimeOffset.UtcNow - terminalSince >= SettleWindow)
                {
                    var finalSnapshot = await ReadTurnStateAsync(jobId, sessionId, ledgerFloor, ct);

                    var finalSignal = ClassifySignals(finalSnapshot);
                    if (finalSignal is not null) return finalSignal;

                    return DeriveFromCompletion(input, finalSnapshot);
                }
            }
            else
            {
                terminalSince = null;
            }

            if (DateTimeOffset.UtcNow - startedAt >= MaxTurnDuration)
            {
                return new TurnOutcome(
                    TurnOutcomeStatus.StuckLoopDetected,
                    $"Turn session {sessionId} for task {input.TaskId} did not conclude within " +
                    $"{MaxTurnDuration.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutes " +
                    $"(last observed status: '{snapshot.SessionStatus}'). Inspect the transcript before " +
                    "resuming; the runner may be wedged on a hung command.");
            }

            await Task.Delay(PollInterval, ct);
        }
    }

    /// <summary>
    /// Everything the outcome is derived from, in one read.
    /// </summary>
    internal record TurnState(
        string SessionStatus,
        IReadOnlyList<ToolCallRecord> ToolCalls,
        IReadOnlyList<JobLedgerRecord> JobMessages,
        DateTimeOffset? LastActivityAt,
        /// <summary>
        /// True when the ledger rows were matched by turn session rather than by time window. False
        /// means the association is inferred and a message from another persona's concurrent turn on
        /// the same job could be misread as this turn's — which callers must account for before
        /// treating a signal as decisive.
        /// </summary>
        bool LedgerScopedToSession = true);

    internal record ToolCallRecord(string ToolName, string? ArgsJson, DateTimeOffset At);

    internal record JobLedgerRecord(string? StructuredPayloadJson, string Content, DateTimeOffset At);

    /// <summary>
    /// Reads the turn's state: session status, the tool calls it recorded, and what it posted to the
    /// job ledger.
    /// </summary>
    /// <remarks>
    /// One method, three queries, all bounded and all <c>AsNoTracking</c>. The tool-call read is
    /// bounded by session (a turn's own transcript) rather than by time, and the job-ledger read is
    /// bounded to the turn's window so a long-running job's history is not rescanned every poll.
    /// </remarks>
    private async Task<TurnState> ReadTurnStateAsync(
        Guid jobId,
        Guid sessionId,
        DateTimeOffset ledgerFloor,
        CancellationToken ct)
    {
        var session = await db.AgentSessions.AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => new { s.Status, s.UpdatedAt, s.CreatedAt })
            .FirstOrDefaultAsync(ct);

        var toolCalls = await db.SessionMessages.AsNoTracking()
            .Where(m => m.SessionId == sessionId && m.EventType == "tool_call")
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.ToolName, m.ToolArgsJson, m.CreatedAt })
            .ToListAsync(ct);

        // The ledger rows this turn produced.
        //
        // Scoped by turn session first, because PlatformMcpTools stamps TurnSessionId on every row it
        // inserts and that association is exact.
        var jobMessages = await db.JobMessages.AsNoTracking()
            .Where(m => m.JobId == jobId && m.TurnSessionId == sessionId)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.StructuredPayloadJson, m.Content, m.CreatedAt, m.TurnSessionId })
            .ToListAsync(ct);

        var ledgerScopedToSession = true;

        if (jobMessages.Count == 0 && session is not null)
        {
            // The exact match found nothing, which has a mundane cause: the session id is resolved from
            // the optional X-Session-Id header (PlatformCallContextResolver), so a runner that does not
            // send it posts to the job ledger with TurnSessionId = null. Filtering strictly on the
            // session would then never see the requires_reply row, the workflow would never pause, and
            // the agent's question would go permanently unanswered — the precise idle-burn this job
            // exists to remove, reintroduced as a filter that is too correct for reality.
            //
            // The fallback is a time window bounded below by this session's own creation, and it
            // excludes rows that are positively attributed to a *different* session. That leaves
            // unattributed rows only, which is the set we are actually looking for.
            var from = ledgerFloor == default ? session.CreatedAt : ledgerFloor;

            jobMessages = await db.JobMessages.AsNoTracking()
                .Where(m => m.JobId == jobId
                            && m.CreatedAt >= from
                            && (m.TurnSessionId == null || m.TurnSessionId == sessionId))
                .OrderBy(m => m.CreatedAt)
                .Select(m => new { m.StructuredPayloadJson, m.Content, m.CreatedAt, m.TurnSessionId })
                .ToListAsync(ct);

            ledgerScopedToSession = false;
        }

        DateTimeOffset? lastActivity = session?.UpdatedAt;

        return new TurnState(
            SessionStatus: session?.Status ?? "unknown",
            ToolCalls: toolCalls
                .Select(t => new ToolCallRecord(t.ToolName ?? string.Empty, t.ToolArgsJson, t.CreatedAt))
                .ToList(),
            JobMessages: jobMessages
                .Select(m => new JobLedgerRecord(m.StructuredPayloadJson, m.Content, m.CreatedAt))
                .ToList(),
            LastActivityAt: lastActivity,
            LedgerScopedToSession: ledgerScopedToSession);
    }

    /// <summary>
    /// Maps recorded activity onto a workflow-visible outcome, or null when the turn has not yet
    /// produced a decisive signal.
    /// </summary>
    /// <remarks>
    /// Precedence is deliberate and is the reason this is one function rather than three checks at the
    /// call site:
    /// <list type="number">
    /// <item><description><c>open_pull_request</c> beats everything, because a PR is an externally
    /// visible artifact and a review gate is the only correct next state. An agent that opened a PR and
    /// then asked a question still needs the PR reviewed; the question is answered in the PR thread.</description></item>
    /// <item><description><c>requires_reply</c> next, because it is an explicit request to block.</description></item>
    /// <item><description>Then the checklist reaching completion, which is the platform's own definition
    /// of "done".</description></item>
    /// </list>
    /// <para>
    /// Only calls made <em>during this turn</em> count. Without that filter, a PR opened on turn two
    /// would put every subsequent turn into <c>PrOpenedAwaitingReview</c> and the workflow would
    /// re-await a review it already consumed — a loop that looks like the agent refusing to move on.
    /// </para>
    /// </remarks>
    internal static TurnOutcome? ClassifySignals(TurnState state)
    {
        // The turn's own tool calls are those recorded after the session's first message. The runner
        // appends tool_call rows as it works, so ordering by time and taking the newest match of each
        // kind is both cheap and correct.
        var prCall = state.ToolCalls
            .Where(t => IsPullRequestTool(t.ToolName))
            .OrderByDescending(t => t.At)
            .FirstOrDefault();

        var chatCall = state.ToolCalls
            .Where(t => IsJobMessageTool(t.ToolName))
            .OrderByDescending(t => t.At)
            .FirstOrDefault();

        // A PR opened through the tool, or a ledger row carrying the PR linkage the tool writes.
        var prFromLedger = state.JobMessages
            .Where(m => HasPrLinkage(m.StructuredPayloadJson))
            .OrderByDescending(m => m.At)
            .FirstOrDefault();

        if (prCall is not null || prFromLedger is not null)
        {
            var (url, number) = ReadPrFacts(prCall?.ArgsJson, prFromLedger?.StructuredPayloadJson);

            // The tool's own result is the authority on whether a PR exists; the call alone is not,
            // because open_pull_request can fail (no credentials, dirty tree) and record the attempt as
            // a tool_call before returning an error payload. Treating the attempt as the outcome would
            // park the workflow on a review of a PR that was never opened.
            if (prFromLedger is not null || LooksSuccessful(prCall?.ArgsJson))
            {
                return TurnOutcome.AwaitingReview(
                    url,
                    number,
                    $"Pull request {(number is null ? url : $"#{number.Value.ToString(CultureInfo.InvariantCulture)}")} " +
                    "opened; awaiting code review.");
            }
        }

        // requires_reply: read from the ledger row the tool wrote, not from the call's arguments. The
        // tool persists {"requiresReply":true,"targetPersona":"astra"} and that row is the durable
        // record that the message actually landed — an argument blob only proves the agent asked.
        //
        // The newest post_job_message row decides, not "any row that requested a reply". An agent can
        // post twice in one turn — "I am blocked on X" then "never mind, unblocked" — and honouring the
        // earlier request would park the workflow on a question the agent has already withdrawn. The
        // human would see "Waiting on input" for a task that is running, which is indistinguishable
        // from the idle-burn this feature exists to remove.
        var replyRequest = state.JobMessages
            .Where(m => HasReplyRequestField(m.StructuredPayloadJson))
            .OrderByDescending(m => m.At)
            .FirstOrDefault();

        if (replyRequest is not null && ReadRequiresReply(replyRequest.StructuredPayloadJson))
        {
            var target = ReadStringProperty(replyRequest.StructuredPayloadJson, "targetPersona") ?? "operator";

            return TurnOutcome.Awaiting(target, TruncateContent(replyRequest.Content));
        }

        // The checklist is the platform's own definition of done: manage_task writes
        // status='completed' and the Cockpit renders the same four values. Deriving completion from it
        // means the workflow and the dashboard cannot disagree about whether the work is finished.
        var allTasksCompleted = state.JobMessages.Any(m => ReadTaskCompletedFlag(m.StructuredPayloadJson));

        if (allTasksCompleted && IsSessionFinished(state.SessionStatus))
        {
            return TurnOutcome.Completed(
                "The agent marked the task complete via manage_task and concluded its turn.");
        }

        return null;
    }

    /// <summary>
    /// The turn ended with no decisive signal: the runner finished and the session went quiet.
    /// </summary>
    /// <remarks>
    /// <see cref="TurnOutcomeStatus.Continue"/> is the default, and that is the important choice. An
    /// agent that ran a turn, edited files, and concluded without opening a PR or asking a question is
    /// mid-task — the ordinary state of work in progress. Treating a quiet turn as
    /// <see cref="TurnOutcomeStatus.TaskCompleted"/> would mark tasks done the moment an agent stopped
    /// typing, which is a worse failure than an extra turn: the extra turn costs tokens, the false
    /// completion silently ships unfinished work and closes the checklist.
    /// <para>
    /// <c>error</c>/<c>stopped</c> are the two statuses that mean the runner did not finish
    /// voluntarily, and those do halt rather than continue: looping on a runner that is crashing would
    /// burn the full turn budget against the same fault.
    /// </para>
    /// </remarks>
    private TurnOutcome DeriveFromCompletion(TaskExecutionInput input, TurnState state)
    {
        var status = state.SessionStatus.ToLowerInvariant();

        if (status is "error" or "stopped")
        {
            return new TurnOutcome(
                TurnOutcomeStatus.Failed,
                $"Turn session ended in status '{state.SessionStatus}' without opening a pull request " +
                "or requesting input. The runner did not finish cleanly; inspect the transcript before " +
                "resuming.",
                PrUrl: null);
        }

        if (status == "waiting_for_input")
        {
            // The session parked without a requires_reply ledger row — an agent that used the driver's
            // own question tool rather than post_job_message. Still a genuine block: waiting on a
            // human who is not in this workflow's inbox would hang the pause forever, so the target is
            // reported as the operator and the message says why.
            return TurnOutcome.Awaiting("operator",
                "The turn parked the session as waiting for input without posting to Job Chat. " +
                "Reply here or in the transcript to resume.");
        }

        return TurnOutcome.ContinueTurn(
            $"Turn concluded (session status '{state.SessionStatus}') with no PR and no pending " +
            "question; continuing to the next step.");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Payload readers
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// True for the tool that opens a pull request.
    /// </summary>
    /// <remarks>
    /// Matched on a normalized name rather than the exact literal: the runner records
    /// <c>ToolName</c> from whatever the driver echoed, and different drivers report the MCP tool with
    /// or without a server prefix (<c>open_pull_request</c> vs
    /// <c>mcp:agentmesh/open_pull_request</c>). A strict equality check silently never matches, and
    /// the failure is a workflow that waits for a review it never learns was requested.
    /// </remarks>
    internal static bool IsPullRequestTool(string? toolName) =>
        NormalizeToolName(toolName).Contains("open_pull_request", StringComparison.OrdinalIgnoreCase) ||
        NormalizeToolName(toolName).Contains("create_pull_request", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for the tool that posts to Job Chat.</summary>
    internal static bool IsJobMessageTool(string? toolName) =>
        NormalizeToolName(toolName).Contains("post_job_message", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeToolName(string? toolName) =>
        (toolName ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// True when a ledger row's payload carries the PR linkage <c>open_pull_request</c> writes.
    /// </summary>
    internal static bool HasPrLinkage(string? structuredPayloadJson) =>
        ReadIntProperty(structuredPayloadJson, "prNumber") is not null ||
        ReadStringProperty(structuredPayloadJson, "prUrl") is not null;

    /// <summary>Reads the PR URL and number, preferring the ledger row the tool wrote.</summary>
    internal static (string? Url, int? Number) ReadPrFacts(string? toolArgsJson, string? ledgerPayloadJson)
    {
        var url = ReadStringProperty(ledgerPayloadJson, "prUrl")
                  ?? ReadStringProperty(toolArgsJson, "pr_url");

        var number = ReadIntProperty(ledgerPayloadJson, "prNumber")
                     ?? ReadIntProperty(toolArgsJson, "pr_number");

        return (url, number);
    }

    /// <summary>
    /// True when a ledger row records a message that asked for a reply.
    /// </summary>
    internal static bool ReadRequiresReply(string? structuredPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(structuredPayloadJson)) return false;

        try
        {
            using var doc = JsonDocument.Parse(structuredPayloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("requiresReply", out var value)) return false;

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                // A string "true" is tolerated because the payload is written by a serializer that
                // could receive the value as text from a driver that stringifies booleans. Anything
                // else is treated as not-requested: defaulting to true would park the workflow on a
                // message that never asked for a reply.
                JsonValueKind.String => string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase),
                JsonValueKind.Number => value.GetInt32() != 0,
                _ => false,
            };
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when a ledger row's payload carries a <c>requiresReply</c> field at all, whatever its value.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ReadRequiresReply"/> because the two answer different questions: this one
    /// identifies a row as a <c>post_job_message</c> row so the newest of them can be found, and that one
    /// reads the verdict it recorded. A PR-linkage row has no such field, which is what stops the two
    /// kinds of ledger row from being ordered against each other and lets "the agent's latest word on
    /// whether it is blocked" be the thing that decides.
    /// </remarks>
    internal static bool HasReplyRequestField(string? structuredPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(structuredPayloadJson)) return false;

        try
        {
            using var doc = JsonDocument.Parse(structuredPayloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "requiresReply", StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>True when a ledger row records the checklist item being marked completed.</summary>
    internal static bool ReadTaskCompletedFlag(string? structuredPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(structuredPayloadJson)) return false;

        try
        {
            using var doc = JsonDocument.Parse(structuredPayloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            // manage_task writes its task view under "task"; a row that says status=completed at the
            // top level is accepted too, because that is the shape a hand-written payload takes.
            var candidates = new List<JsonElement> { doc.RootElement };

            if (doc.RootElement.TryGetProperty("task", out var task) &&
                task.ValueKind == JsonValueKind.Object)
            {
                candidates.Add(task);
            }

            foreach (var candidate in candidates)
            {
                if (ReadStringProperty(candidate, "status") == JobTaskStatus.Completed) return true;
                if (candidate.TryGetProperty("isCompleted", out var flag) &&
                    flag.ValueKind == JsonValueKind.True)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when a tool call's recorded arguments do not indicate a failure.
    /// </summary>
    /// <remarks>
    /// Weak by construction, and deliberately so: <c>ToolArgsJson</c> holds the arguments the model
    /// sent, not the result, so it cannot prove success. What it can prove is that the call was
    /// malformed (no title, no branch), and those are the cases where the tool certainly did not open
    /// a PR. Anything else is treated as "assume the ledger", which is why the ledger check runs first
    /// in <see cref="ClassifySignals"/>.
    /// </remarks>
    internal static bool LooksSuccessful(string? toolArgsJson)
    {
        if (string.IsNullOrWhiteSpace(toolArgsJson)) return true; // no evidence either way; defer to the ledger

        try
        {
            using var doc = JsonDocument.Parse(toolArgsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return true;

            // open_pull_request requires a title; a call without one was rejected before any push.
            if (doc.RootElement.TryGetProperty("title", out var title) &&
                title.ValueKind == JsonValueKind.String &&
                string.IsNullOrWhiteSpace(title.GetString()))
            {
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static string? ReadStringProperty(string? json, string property)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? ReadStringProperty(doc.RootElement, property)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadStringProperty(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value))
        {
            // Case-insensitive fallback: the payload is written with a camelCase policy but read back
            // from a column another writer may have populated with PascalCase.
            foreach (var candidate in root.EnumerateObject())
            {
                if (!string.Equals(candidate.Name, property, StringComparison.OrdinalIgnoreCase)) continue;
                return candidate.Value.ValueKind == JsonValueKind.String
                    ? candidate.Value.GetString()
                    : candidate.Value.ToString();
            }

            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static int? ReadIntProperty(string? json, string property)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            var raw = ReadStringProperty(doc.RootElement, property);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsSessionFinished(string status) =>
        TerminalStatuses.Contains(status, StringComparer.OrdinalIgnoreCase);

    private static string TruncateContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "The agent posted a message and is waiting for a reply.";

        var trimmed = content.Trim();
        return trimmed.Length <= 1_000 ? trimmed : trimmed[..1_000] + "…";
    }

    private static string RedactTruncate(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(empty response body)";

        var trimmed = body.Trim();
        // The dispatch response and its error body are logged and then travel into an activity failure,
        // which lands in Temporal's history — readable by anyone with console access. An API key echoed
        // by a proxy error page would otherwise be recorded permanently.
        trimmed = System.Text.RegularExpressions.Regex.Replace(
            trimmed, @"(api[_-]?key|authorization|bearer|token)[""'\s:=]+[^""'\s,}]+",
            "$1=[redacted]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return trimmed.Length <= 400 ? trimmed : trimmed[..400] + "…";
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or ObjectDisposedException)
        {
            return string.Empty;
        }
    }

    private static Guid? TryReadGuidProperty(string? json, string property)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            var node = JsonNode.Parse(json);
            if (node?[property] is null)
            {
                // Fall back to a case-insensitive scan; the response is serialized camelCase today but
                // an anonymous-object default would be PascalCase.
                if (node is JsonObject obj)
                {
                    foreach (var pair in obj)
                    {
                        if (!string.Equals(pair.Key, property, StringComparison.OrdinalIgnoreCase)) continue;
                        node = pair.Value;
                        break;
                    }
                }
                else
                {
                    return null;
                }
            }

            var raw = node?.GetValueKind() == JsonValueKind.String ? node?.GetValue<string>() : node?.ToString();
            return Guid.TryParse(raw?.Trim('"'), out var parsed) ? parsed : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records progress against the activity's heartbeat timeout.
    /// </summary>
    /// <remarks>
    /// Guarded by <see cref="ActivityExecutionContext.HasCurrent"/> because the same runner is called
    /// directly in unit tests, where there is no activity context to heartbeat against. Swallowing the
    /// failure rather than letting it escape is also deliberate: a heartbeat that cannot be recorded
    /// (the task token expired because the server already gave up on this attempt) says nothing the
    /// poll loop will not discover on its own, and throwing here would replace a real turn failure with
    /// a bookkeeping one.
    /// </remarks>
    private static void Heartbeat(string detail)
    {
        if (!ActivityExecutionContext.HasCurrent) return;

        try
        {
            ActivityExecutionContext.Current.Heartbeat(new object?[] { detail });
        }
        catch (Exception)
        {
            // Deliberately silent: see the remark above.
        }
    }
}
