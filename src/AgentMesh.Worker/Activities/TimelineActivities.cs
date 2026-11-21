using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentMesh.Core.Activities;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Worker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Temporalio.Activities;

namespace AgentMesh.Worker.Activities;

public class TimelineActivities(
    AgentMeshDbContext dbContext,
    ILogger<TimelineActivities> logger,
    IPromptStackBuilder? promptStackBuilder = null,
    IAgentTurnRunner? turnRunner = null,
    IHttpClientFactory? httpClientFactory = null,
    IConfiguration? config = null) : ITimelineActivities
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Whether an escalation card is addressed to the human operator (as opposed to another
    /// persona). The payload's <c>targetPersona</c> written by <c>post_job_message</c> decides;
    /// cards predating that field are matched on the "Escalation from @x to @operator" title,
    /// which is the fallback <see cref="TimelineCardType.Escalation3R"/> rendering itself uses.
    /// </summary>
    private static bool EscalationTargetsOperator(JobTimelineCard card)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(card.StructuredPayloadJson))
            {
                using var doc = JsonDocument.Parse(card.StructuredPayloadJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("targetPersona", out var target) &&
                    target.ValueKind == JsonValueKind.String)
                {
                    return string.Equals(target.GetString(), "operator", StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch
        {
            // An unreadable payload is no reason to guess wrong; fall through to the title.
        }

        return (card.Title ?? string.Empty).Contains("to @operator", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Adds a <c>resolutionNote</c> to a card payload, preserving everything already recorded, so
    /// a card closed by the platform says why on the card itself.
    /// </summary>
    private static string WithResolutionNote(string payloadJson, string note)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(payloadJson)) return payloadJson;
            if (JsonNode.Parse(payloadJson) is not JsonObject obj) return payloadJson;
            obj["resolutionNote"] = note;
            return obj.ToJsonString(JsonOptions);
        }
        catch
        {
            return payloadJson;
        }
    }

    private async Task<ProjectJob?> TryLoadJobAsync(string jobId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(jobId?.Trim(), out var parsedId))
        {
            return null;
        }

        return await dbContext.ProjectJobs.FirstOrDefaultAsync(j => j.Id == parsedId, ct);
    }

    private async Task NotifyCardUpdatedAsync(JobTimelineCard card)
    {
        if (httpClientFactory == null || config == null)
        {
            return;
        }

        try
        {
            var apiBaseUrl = HttpAgentTurnRunner.ResolveApiBaseUrl(config);
            if (string.IsNullOrWhiteSpace(apiBaseUrl))
            {
                return;
            }

            var client = httpClientFactory.CreateClient(HttpAgentTurnRunner.HttpClientName);
            var url = $"{apiBaseUrl.TrimEnd('/')}/api/jobs/{Uri.EscapeDataString(card.JobId)}/timeline/cards/{card.Id}/notify-updated";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            var apiKey = HttpAgentTurnRunner.ResolveApiKey(config);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
            }

            using var resp = await client.SendAsync(req);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Best-effort timeline card broadcast skipped for card {CardId}", card.Id);
        }
    }

    [Activity]
    public async Task<JobTimelineCard> CreateCardAsync(CreateTimelineCardInput input)
    {
        // One card per pull request, whichever writer arrives first.
        //
        // The review gate and the `pr open` path both post a PrReview card, and each one deduplicated
        // against a different notion of "the card for this PR" — the gate against workflow state that a
        // new run starts empty, the CLI path against the pull-request number in the payload. A job that
        // reached the pull-request stage therefore got two cards sitting side by side in the cockpit
        // saying the same thing, and the poller had to guess which one's timestamp is the real park.
        //
        // Doing it here, inside the activity, rather than in the workflow, is deliberate: the workflow's
        // command sequence is unchanged, so a run parked mid-review across a deploy replays exactly as
        // it did before. Only the effect on the table changes.
        if (input.CardType == TimelineCardType.PrReview)
        {
            var adopted = await AdoptExistingPullRequestCardAsync(input);
            if (adopted is not null)
            {
                return adopted;
            }
        }

        var card = new JobTimelineCard
        {
            Id = Guid.NewGuid(),
            JobId = input.JobId,
            TaskId = input.TaskId,
            CardType = input.CardType,
            Status = input.Status,
            Title = input.Title,
            StructuredPayloadJson = input.StructuredPayloadJson,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        dbContext.JobTimelineCards.Add(card);

        var job = await TryLoadJobAsync(input.JobId);
        if (job != null)
        {
            job.ProgressHeartbeat = input.Title;
            if (input.CardType == TimelineCardType.JobInitialized && job.Status == ProjectJobStatus.Pending)
            {
                job.Status = ProjectJobStatus.Active;
            }
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await dbContext.SaveChangesAsync();
        await NotifyCardUpdatedAsync(card);

        logger.LogInformation("Created timeline card {CardId} for job {JobId} of type {CardType}", 
            card.Id, card.JobId, card.CardType);

        return card;
    }

    /// <summary>
    /// Updates the job's existing pull-request review card instead of inserting a second one, or returns
    /// null when this job has no card for this pull request yet.
    /// </summary>
    private async Task<JobTimelineCard?> AdoptExistingPullRequestCardAsync(
        CreateTimelineCardInput input,
        CancellationToken ct = default)
    {
        var reference = PrReviewCardPayload.ReadReference(input.StructuredPayloadJson);
        if (reference.Number is null && reference.Url is null)
        {
            // The gate can park before a pull request is registered; with nothing to match on, creating
            // a card is the only honest option. See PrReviewCardPayload for why guessing is worse.
            return null;
        }

        // Filtered in memory on purpose. StructuredPayloadJson is jsonb and PostgreSQL defines no LIKE
        // for jsonb, so a payload predicate translates to SQL the server rejects — the same constraint
        // the session-transcript scan in this class documents. One job has a handful of review cards.
        var candidates = await dbContext.JobTimelineCards
            .Where(c => c.JobId == input.JobId && c.CardType == TimelineCardType.PrReview)
            .ToListAsync(ct);

        var existing = candidates
            .Where(c => PrReviewCardPayload.IsSamePullRequest(
                PrReviewCardPayload.ReadReference(c.StructuredPayloadJson), reference))
            .OrderByDescending(c => c.UpdatedAt)
            .FirstOrDefault();

        if (existing is null)
        {
            return null;
        }

        var wasMissingTask = string.IsNullOrWhiteSpace(existing.TaskId);
        existing.Status = input.Status;
        existing.Title = input.Title ?? existing.Title;
        existing.StructuredPayloadJson =
            PrReviewCardPayload.Merge(existing.StructuredPayloadJson, input.StructuredPayloadJson);
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        // The gate parks with no task and the CLI writes with one, so whichever lands second should fill
        // the gap rather than erase the other's context: task id drives which checklist row the card
        // renders under.
        if (wasMissingTask && !string.IsNullOrWhiteSpace(input.TaskId))
        {
            existing.TaskId = input.TaskId;
        }

        var job = await TryLoadJobAsync(input.JobId, ct);
        if (job != null)
        {
            job.ProgressHeartbeat = input.Title;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await dbContext.SaveChangesAsync(ct);
        await NotifyCardUpdatedAsync(existing);

        logger.LogInformation(
            "Adopted existing pull-request review card {CardId} for job {JobId} (PR {PrNumber} {PrUrl}) " +
            "instead of posting a duplicate",
            existing.Id, existing.JobId,
            reference.Number?.ToString() ?? "-", reference.Url ?? "-");

        return existing;
    }

    [Activity]
    public async Task UpdateCardStatusAsync(UpdateTimelineCardStatusInput input)
    {
        var card = await dbContext.JobTimelineCards.FindAsync(input.CardId);
        if (card != null)
        {
            card.Status = input.Status;
            card.UpdatedAt = DateTimeOffset.UtcNow;
            if (!string.IsNullOrEmpty(input.StructuredPayloadJson))
            {
                // The review card has two writers, and a status write from the gate carries no linkage
                // fields. Replacing the payload would delete the pull-request summary the CLI recorded
                // from a card that was just displaying it, so this card type merges and every other one
                // keeps the replace semantics its callers already depend on.
                card.StructuredPayloadJson = card.CardType == TimelineCardType.PrReview
                    ? PrReviewCardPayload.Merge(card.StructuredPayloadJson, input.StructuredPayloadJson)
                    : input.StructuredPayloadJson;
            }
            if (!string.IsNullOrEmpty(input.Title))
            {
                card.Title = input.Title;
            }

            var job = await TryLoadJobAsync(card.JobId);
            if (job != null)
            {
                if (card.CardType == TimelineCardType.JobInitialized && input.Status == TimelineCardStatus.Resolved)
                {
                    job.Status = ProjectJobStatus.Completed;
                    job.ProgressHeartbeat = "Pull request merged; job completed";
                }
                else if (card.CardType == TimelineCardType.PlanApproval && input.Status == TimelineCardStatus.Approved)
                {
                    job.ProgressHeartbeat = "Plan approved. Starting implementation.";
                }
                job.UpdatedAt = DateTimeOffset.UtcNow;
            }

            if (card.CardType == TimelineCardType.JobInitialized &&
                input.Status == TimelineCardStatus.Resolved &&
                Guid.TryParse(card.JobId?.Trim(), out var agentJobGuid))
            {
                var agentJob = await dbContext.AgentJobs.FirstOrDefaultAsync(j => j.Id == agentJobGuid);
                if (agentJob != null)
                {
                    agentJob.ExecutionStatus = ExecutionStatus.Completed;
                    agentJob.ProgressHeartbeat = "Pull request merged; job completed";
                    agentJob.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }

            await dbContext.SaveChangesAsync();
            await NotifyCardUpdatedAsync(card);

            logger.LogInformation("Updated timeline card {CardId} status to {Status}", card.Id, card.Status);
        }
        else
        {
            logger.LogWarning("Timeline card {CardId} not found to update status", input.CardId);
        }
    }

    /// <summary>
    /// The sessions whose transcript belongs to this job, newest first.
    /// </summary>
    /// <remarks>
    /// <c>agent_sessions.metadata</c> is a jsonb column and PostgreSQL defines no <c>LIKE</c> for jsonb,
    /// so a <c>Contains</c> over <see cref="AgentSession.MetadataJson"/> inside a <c>where</c> renders SQL
    /// the server rejects (<c>42883: operator does not exist: jsonb ~~ jsonb</c>). The match therefore runs
    /// in memory over the same bounded, newest-first scan the prompt composer performs on this column, and
    /// shares that matcher so discovery and composition cannot disagree about which transcript is this
    /// job's. The scan is capped rather than complete because discovery runs directly behind its own turn,
    /// which puts the session that matters at the front of it; the transcript reads then filter on the
    /// indexed <c>session_messages.session_id</c>.
    /// </remarks>
    private async Task<List<Guid>> ResolveJobSessionIdsAsync(Guid jobId, CancellationToken ct)
    {
        try
        {
            var candidates = await dbContext.AgentSessions
                .AsNoTracking()
                .Where(s => s.UpdatedAt >= DateTimeOffset.UtcNow.AddDays(-30))
                .OrderByDescending(s => s.UpdatedAt)
                .Take(200)
                .Select(s => new { s.Id, s.MetadataJson })
                .ToListAsync(ct);

            return candidates
                .Where(s => PromptStackBuilder.MetadataMatchesJob(s.MetadataJson, jobId))
                .Select(s => s.Id)
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Session lookup by job {JobId} failed during discovery", jobId);
            return [];
        }
    }

    /// <summary>
    /// The transcript rows for a set of sessions, before the caller narrows to one shape.
    /// </summary>
    /// <remarks>
    /// The job filter arrives as already-resolved session ids and never as a predicate on
    /// <c>agent_sessions.metadata</c>, because joining to name the job would put a <c>LIKE</c> on a jsonb
    /// column in the WHERE clause, which PostgreSQL rejects outright. Rendering this builder through the
    /// Npgsql provider is what <c>DiscoveryTranscriptSqlTranslationTests</c> does — the in-memory provider
    /// the activity tests run on translates anything, which is how an untranslatable predicate lived here
    /// unnoticed.
    /// </remarks>
    internal static IQueryable<SessionMessage> TranscriptSource(AgentMeshDbContext db, List<Guid> sessionIds) =>
        db.SessionMessages.AsNoTracking().Where(m => sessionIds.Contains(m.SessionId));

    /// <summary>
    /// Reads one transcript source without letting its failure end discovery.
    /// </summary>
    /// <remarks>
    /// Only cancellation escapes. Anything else means this one source is unreadable, which is a partial
    /// result rather than a reason to abandon the stage — the alternative is a five-deep chain of
    /// try/catches around code whose whole job is to describe what the agent did.
    /// </remarks>
    private async Task<T?> ReadTranscriptAsync<T>(string source, Func<Task<T>> read, string jobId, CancellationToken ct)
    {
        try
        {
            return await read();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Discovery transcript source {Source} could not be read for job {JobId}; continuing with the remaining sources",
                source, jobId);
            return default;
        }
    }

    /// <summary>
    /// The literal a Stage 1 turn replies with to say it looked and has nothing to ask.
    /// </summary>
    /// <remarks>
    /// Matched verbatim, not inferred. The point of a sentinel is that "no questions" becomes something
    /// the agent produced rather than something the platform concluded from an absence, which is the only
    /// way the absence of a Discovery Q&A card means anything at all.
    /// </remarks>
    internal const string NoQuestionsSentinel = "NO_OPEN_QUESTIONS";

    /// <summary>
    /// The follow-up turn sent when Stage 1 ends with neither a structured question nor a confirmation.
    /// </summary>
    internal const string NoQuestionsRepromptText =
        "Your Stage 1 turn ended with no ask_question call and no fenced ask_question block, so the " +
        "platform has nothing it is allowed to show the operator. Answer now in exactly one of two forms:\n" +
        "1. Open questions exist and their answers would change the work: emit them this turn with " +
        "ask_question (one entry per question, each with its options), or — with no function tools — a " +
        "fenced ask_question JSON block. Do not restate them in prose.\n" +
        "2. There are no open questions: reply with " + NoQuestionsSentinel + " on a line of its own.\n" +
        "Anything else is read as form 2 and the job advances to the plan gate on your assumptions. Ask " +
        "only what would change the work: a question the operator must answer costs them a turn, so " +
        "manufacturing one to look careful is a cost, not a courtesy. Re-read whatever you need to " +
        "decide, but this is a confirmation, not a fresh discovery phase.";

    /// <summary>
    /// Asks a Stage 1 turn that asked nothing to either ask or confirm, once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because a silent Discovery Q&A is unfalsifiable: an agent with nothing to ask and an
    /// agent that asked in prose both leave the same transcript shape, and the operator sees the same
    /// missing card. The nudge turns that into a checkable event with two sanctioned exits, and stops
    /// there — one follow-up, because a stage that needs two rounds of prompting to state a question has
    /// no question, and a loop would charge the operator a persona turn per round to find that out.
    /// </para>
    /// <para>
    /// It re-dispatches rather than resuming: Stage 1B already runs this way (a fresh session whose
    /// context is the composed prompt stack plus one incoming message), so the nudge costs the same as
    /// any other turn in this phase and does not depend on the driver keeping session state the platform
    /// cannot see.
    /// </para>
    /// </remarks>
    /// <returns>
    /// Whatever the follow-up asked through a sanctioned channel, whether the reply carried the sentinel,
    /// and the reply text for the card summary.
    /// </returns>
    private async Task<(List<DiscoveryQuestion> Questions, bool Confirmed, string? Reply)> RunNoQuestionsRepromptAsync(
        TaskExecutionInput taskInput,
        Guid jobId,
        CancellationToken ct)
    {
        if (turnRunner is null || promptStackBuilder is null)
        {
            return ([], false, null);
        }

        try
        {
            var pendingReply = new ChatMessageSignal("operator", NoQuestionsRepromptText, DateTimeOffset.UtcNow);
            var context = await promptStackBuilder.BuildPromptStackAsync(
                PromptStackContext.FromTaskInput(taskInput, pendingReply), ct);

            var follow = await turnRunner.RunTurnAsync(new AgentTurnRequest(
                Input: taskInput,
                Stack: context,
                WorkspacePath: context.WorkspacePath,
                PersonaId: taskInput.PersonaId,
                AgentId: taskInput.AgentId), ct);

            var reply = follow.Message;
            var sessionIds = await ResolveJobSessionIdsAsync(jobId, ct);
            var found = new List<DiscoveryQuestion>();
            var texts = new List<string>();

            if (sessionIds.Count > 0)
            {
                var toolArgs = await ReadTranscriptAsync("ask_question tool call (follow-up)", () =>
                    TranscriptSource(dbContext, sessionIds)
                        .Where(m => m.ToolName == "ask_question")
                        .OrderByDescending(m => m.CreatedAt)
                        .Select(m => m.ToolArgsJson)
                        .FirstOrDefaultAsync(ct), taskInput.JobId, ct);

                var response = await ReadTranscriptAsync("response (follow-up)", () =>
                    TranscriptSource(dbContext, sessionIds)
                        .Where(m => m.EventType == "response")
                        .OrderByDescending(m => m.CreatedAt)
                        .Select(m => m.Content)
                        .FirstOrDefaultAsync(ct), taskInput.JobId, ct);

                if (!string.IsNullOrWhiteSpace(response)) texts.Add(response);
                if (!string.IsNullOrWhiteSpace(toolArgs))
                {
                    found = ExtractQuestionsFromText(toolArgs);
                }
            }

            if (!string.IsNullOrWhiteSpace(reply)) texts.Add(reply);

            if (found.Count == 0)
            {
                foreach (var text in texts)
                {
                    found = ExtractQuestionsFromText(text);
                    if (found.Count > 0) break;
                }
            }

            var confirmed = texts.Any(t => t.Contains(NoQuestionsSentinel, StringComparison.Ordinal));
            return (found, found.Count == 0 && confirmed, reply);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The nudge is the stage's last attempt, not a dependency. Failing it must not fail a
            // discovery turn that already ran: the job proceeds exactly as it did before this existed,
            // and the unconfirmed-silence warning says so.
            logger.LogWarning(ex,
                "The no-questions follow-up turn could not run for job {JobId}; discovery ends unconfirmed",
                taskInput.JobId);
            return ([], false, null);
        }
    }

    [Activity]
    public async Task<DiscoveryQaActivityResult> ExecuteDiscoveryQaActivityAsync(DiscoveryQaActivityInput input)
    {
        var ct = ActivityExecutionContext.HasCurrent
            ? ActivityExecutionContext.Current.CancellationToken
            : default;

        var job = await TryLoadJobAsync(input.JobId, ct);
        string? agentSummary = null;
        string? recentMessage = null;
        var questions = new List<DiscoveryQuestion>();
        // Set only when the agent said the quiet part explicitly: it looked, and it has nothing to ask.
        // That is a different thing from "no question was found", and it earns a different log line.
        var noQuestionsConfirmed = false;
        // Hoisted out of the turn block so an empty result can name the transcript it actually searched.
        var jobSessionIds = new List<Guid>();

        if (turnRunner != null && promptStackBuilder != null && job != null)
        {
            try
            {
                job.ProgressHeartbeat = "Exploring codebase and preparing discovery Q&A";
                job.UpdatedAt = DateTimeOffset.UtcNow;
                await dbContext.SaveChangesAsync(ct);

                var agentId = !string.IsNullOrWhiteSpace(job.AssignedAgentId) ? job.AssignedAgentId! : "rook";
                var taskInput = new TaskExecutionInput(
                    JobId: input.JobId,
                    TaskId: "stage-1-discovery",
                    AgentId: agentId,
                    ProjectId: job.ProjectId,
                    WorkspacePath: input.WorkspacePath,
                    TargetBranch: !string.IsNullOrWhiteSpace(job.GitBranchName) ? job.GitBranchName : "main",
                    PersonaId: agentId,
                    TaskIndex: 0,
                    TaskTitle: "Stage 1A: Discovery Q&A"
                );

                var context = await promptStackBuilder.BuildPromptStackAsync(
                    PromptStackContext.FromTaskInput(taskInput, null), ct);

                var request = new AgentTurnRequest(
                    Input: taskInput,
                    Stack: context,
                    WorkspacePath: context.WorkspacePath,
                    PersonaId: taskInput.PersonaId,
                    AgentId: taskInput.AgentId);

                var outcome = await turnRunner.RunTurnAsync(request, ct);
                agentSummary = outcome.Message;

                var latestCardPayloads = await dbContext.JobTimelineCards
                    .AsNoTracking()
                    .Where(c => c.JobId == job.Id.ToString() &&
                                (c.CardType == TimelineCardType.Escalation3R ||
                                 c.CardType == TimelineCardType.InternalConsultation ||
                                 c.CardType == TimelineCardType.TaskStep))
                    .OrderByDescending(c => c.CreatedAt)
                    .Select(c => c.StructuredPayloadJson)
                    .Take(5)
                    .ToListAsync(ct);

                foreach (var cardPayload in latestCardPayloads)
                {
                    if (string.IsNullOrWhiteSpace(cardPayload)) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(cardPayload);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                            doc.RootElement.TryGetProperty("content", out var contentProp) &&
                            contentProp.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(contentProp.GetString()))
                        {
                            recentMessage = contentProp.GetString();
                            break;
                        }
                    }
                    catch
                    {
                        // Ignore malformed card payload
                    }
                }

                // Two channels, and both are structured: the ask_question tool call, which function-tool
                // drivers make and the platform records as tool-call args, and — for subprocess drivers
                // like claude -p, agy and copilot, which have no tool schemas — a fenced ask_question
                // block inside text the turn itself produced. The reasoning stream is searched by nobody:
                // it is where an agent thinks out loud, so a question scraped out of it is the least
                // trustworthy thing in the transcript. Each read is guarded so one source the database
                // cannot serve degrades to "nothing here" instead of taking the others down with it.
                jobSessionIds = await ResolveJobSessionIdsAsync(job.Id, ct);

                if (jobSessionIds.Count > 0)
                {
                    var recentToolCallArgs = await ReadTranscriptAsync("ask_question tool call", () =>
                        TranscriptSource(dbContext, jobSessionIds)
                            .Where(m => m.ToolName == "ask_question")
                            .OrderByDescending(m => m.CreatedAt)
                            .Select(m => m.ToolArgsJson)
                            .FirstOrDefaultAsync(ct), input.JobId, ct);

                    if (!string.IsNullOrWhiteSpace(recentToolCallArgs))
                    {
                        questions = ExtractQuestionsFromText(recentToolCallArgs);
                    }
                }

                if (questions.Count == 0 && jobSessionIds.Count > 0)
                {
                    var recentSessionResponse = await ReadTranscriptAsync("response", () =>
                        TranscriptSource(dbContext, jobSessionIds)
                            .Where(m => m.EventType == "response")
                            .OrderByDescending(m => m.CreatedAt)
                            .Select(m => m.Content)
                            .FirstOrDefaultAsync(ct), input.JobId, ct);

                    if (!string.IsNullOrWhiteSpace(recentSessionResponse))
                    {
                        questions = ExtractQuestionsFromText(recentSessionResponse);
                        if (recentSessionResponse.Contains(NoQuestionsSentinel, StringComparison.Ordinal))
                        {
                            noQuestionsConfirmed = true;
                        }
                        if (string.IsNullOrWhiteSpace(agentSummary) || agentSummary.StartsWith("Turn concluded"))
                        {
                            agentSummary = recentSessionResponse;
                        }
                    }
                }

                if (questions.Count == 0)
                {
                    foreach (var authored in new[] { outcome.Message, recentMessage })
                    {
                        if (string.IsNullOrWhiteSpace(authored)) continue;
                        if (authored.Contains(NoQuestionsSentinel, StringComparison.Ordinal))
                        {
                            noQuestionsConfirmed = true;
                        }
                        questions = ExtractQuestionsFromText(authored);
                        if (questions.Count > 0) break;
                    }
                }

                // Nothing was found on any sanctioned channel, which is precisely the state the operator
                // cannot see from the cockpit: the job walks past Discovery Q&A into a plan built on
                // assumptions nobody confirmed. Guessing over the prose to fill the gap is worse, because
                // a card assembled from a sentence the agent never meant as a question steers the job
                // with words it did not say. So the stage asks once, in the open, and accepts only the two
                // answers it can check mechanically.
                if (questions.Count == 0 && !noQuestionsConfirmed &&
                    outcome.Status is not (TurnOutcomeStatus.Failed or TurnOutcomeStatus.StuckLoopDetected))
                {
                    // AwaitingInput is the one silence that must not be nudged: the turn parked with a human
                    // owing it an answer. The nudge is a fresh turn on the same persona, so it does not ask
                    // the question — it competes with the turn that is already holding it, and the answer the
                    // operator gives lands on a transcript this stage stopped reading. Job c28eca57 did
                    // exactly this in November 2026: two questions rendered in the session chat, the operator
                    // answered them, and the job had already walked into the plan gate five seconds after the
                    // agent asked. Warning here is honest; dispatching a rival turn is not.
                    if (outcome.Status == TurnOutcomeStatus.AwaitingInput)
                    {
                        logger.LogWarning(
                            "Discovery turn for job {JobId} parked on an unanswered question this stage could not read " +
                            "({SessionCount} session(s) [{SessionIds}]). The no-questions follow-up turn is skipped: it would run " +
                            "against the persona that is holding the question. The job advances on the agent's assumptions.",
                            input.JobId, jobSessionIds.Count, string.Join(", ", jobSessionIds));
                    }
                    else
                    {
                        var reprompt = await RunNoQuestionsRepromptAsync(taskInput, job.Id, ct);
                        questions = reprompt.Questions;
                        noQuestionsConfirmed = reprompt.Confirmed;

                        if (questions.Count > 0 &&
                            (string.IsNullOrWhiteSpace(agentSummary) || agentSummary.StartsWith("Turn concluded")) &&
                            !string.IsNullOrWhiteSpace(reprompt.Reply))
                        {
                            agentSummary = reprompt.Reply;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Agent discovery turn encountered an error for job {JobId}", input.JobId);
            }
        }

        if (questions.Count == 0)
        {
            // Two different silences. A confirmed one is the stage working as designed: the agent looked,
            // found nothing worth asking, and produced the token that says so. An unconfirmed one is the
            // fault the operator cannot see from the cockpit — Discovery Q&A was skipped and nobody,
            // including this activity, knows whether that was a decision or an omission — so it warns, and
            // names the transcript worth reading.
            if (noQuestionsConfirmed)
            {
                logger.LogInformation(
                    "Discovery turn for job {JobId} confirmed {Sentinel}: it has no open questions; skipping DiscoveryQa card",
                    input.JobId, NoQuestionsSentinel);
            }
            else
            {
                logger.LogWarning(
                    "Discovery turn for job {JobId} asked no structured questions on {SessionCount} session(s) [{SessionIds}], and its follow-up turn produced neither an ask_question nor {Sentinel} — the job advances to the plan gate on the agent's assumptions",
                    input.JobId, jobSessionIds.Count, string.Join(", ", jobSessionIds), NoQuestionsSentinel);
            }

            var emptyPayload = new DiscoveryPayload(questions, agentSummary ?? "Discovery completed without clarifying questions.");
            return new DiscoveryQaActivityResult(Guid.Empty, emptyPayload);
        }

        var summary = !string.IsNullOrWhiteSpace(agentSummary) && !agentSummary.StartsWith("Turn concluded")
            ? agentSummary!
            : (!string.IsNullOrWhiteSpace(recentMessage)
                ? recentMessage!
                : $"Discovery questionnaire for initial prompt: {input.InitialPrompt}");

        var payload = new DiscoveryPayload(questions, summary);
        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);

        var card = new JobTimelineCard
        {
            Id = Guid.NewGuid(),
            JobId = input.JobId,
            CardType = TimelineCardType.DiscoveryQa,
            Status = TimelineCardStatus.PendingInput,
            Title = "Discovery Q&A: Clarify Task Requirements",
            StructuredPayloadJson = payloadJson,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        dbContext.JobTimelineCards.Add(card);

        if (job != null)
        {
            job.ProgressHeartbeat = "Awaiting discovery Q&A answers";
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await dbContext.SaveChangesAsync(ct);
        await NotifyCardUpdatedAsync(card);

        logger.LogInformation("Created DiscoveryQa card {CardId} for job {JobId} with {QuestionCount} questions", 
            card.Id, input.JobId, questions.Count);

        return new DiscoveryQaActivityResult(card.Id, payload);
    }

    [Activity]
    public async Task<DecompositionActivityResult> ExecuteDecompositionActivityAsync(DecompositionActivityInput input)
    {
        var ct = ActivityExecutionContext.HasCurrent
            ? ActivityExecutionContext.Current.CancellationToken
            : default;

        var job = await TryLoadJobAsync(input.JobId, ct);
        var tasks = new List<ProposedChecklistItem>();
        string? agentReasoning = null;

        if (turnRunner != null && promptStackBuilder != null && job != null)
        {
            try
            {
                job.ProgressHeartbeat = string.IsNullOrWhiteSpace(input.Feedback)
                    ? "Decomposing job into implementation tasks"
                    : "Re-decomposing implementation plan with operator feedback";
                job.UpdatedAt = DateTimeOffset.UtcNow;
                await dbContext.SaveChangesAsync(ct);

                var existingTasks = ParseTasksFromJobJson(job.TasksJson);
                if (existingTasks.Count == 0 || !string.IsNullOrWhiteSpace(input.Feedback))
                {
                    var answersSummary = input.Answers is { Count: > 0 }
                        ? string.Join("\n", input.Answers.Select(kv => $"- {kv.Key}: {kv.Value}"))
                        : "Use standard repository defaults.";

                    var replyContent = string.IsNullOrWhiteSpace(input.Feedback)
                        ? $"Discovery Q&A Answers:\n{answersSummary}\n\nPlease decompose this job into atomic checklist tasks using manage_task(action: \"set_checklist\")."
                        : $"Plan Revision Feedback:\n{input.Feedback}\n\nDiscovery Q&A Answers:\n{answersSummary}\n\nPlease update the checklist tasks using manage_task(action: \"set_checklist\").";

                    var pendingReply = new ChatMessageSignal("operator", replyContent, DateTimeOffset.UtcNow);
                    var agentId = !string.IsNullOrWhiteSpace(job.AssignedAgentId) ? job.AssignedAgentId! : "rook";

                    var taskInput = new TaskExecutionInput(
                        JobId: input.JobId,
                        TaskId: "stage-1-decomposition",
                        AgentId: agentId,
                        ProjectId: job.ProjectId,
                        WorkspacePath: input.WorkspacePath,
                        TargetBranch: !string.IsNullOrWhiteSpace(job.GitBranchName) ? job.GitBranchName : "main",
                        PersonaId: agentId,
                        TaskIndex: 0,
                        TaskTitle: "Stage 1B: Task Decomposition"
                    );

                    var context = await promptStackBuilder.BuildPromptStackAsync(
                        PromptStackContext.FromTaskInput(taskInput, pendingReply), ct);

                    var request = new AgentTurnRequest(
                        Input: taskInput,
                        Stack: context,
                        WorkspacePath: context.WorkspacePath,
                        PersonaId: taskInput.PersonaId,
                        AgentId: taskInput.AgentId);

                    var outcome = await turnRunner.RunTurnAsync(request, ct);
                    agentReasoning = outcome.Message;

                    await dbContext.Entry(job).ReloadAsync(ct);
                }

                tasks = ParseTasksFromJobJson(job.TasksJson);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // No invented breakdown follows: the plan gate says so, and the workflow halts rather than
                // spending the operator's approval on tasks nobody proposed.
                logger.LogWarning(ex, "Agent decomposition turn encountered an error for job {JobId}; the plan gate will report that no checklist was registered", input.JobId);
            }
        }
        else if (job != null)
        {
            tasks = ParseTasksFromJobJson(job.TasksJson);
        }

        // An empty checklist is reported, never papered over. In November 2026 a Stage 1B decomposition on
        // the agy driver called that harness's own built-in `manage_task` — a background-shell manager — and
        // "succeeded", so the job reached the gate with these four AgentMesh-shaped placeholders standing in
        // for a budget app nobody had planned. Approving them would have spent real turns on files that do
        // not exist in that repo, and the operator had no way to see the substitution.
        var noChecklist = tasks.Count == 0;
        var reasoning = noChecklist
            ? "No checklist was registered, so there is nothing to approve. The decomposition turn ended " +
              "without manage_task(action: \"set_checklist\") reaching the platform — a driver with no function " +
              "tools (agy, claude -p, copilot) must use `agentmesh task set-checklist` from its shell instead. " +
              "Nothing was invented to fill the gap: rejecting with feedback re-runs the decomposition, and " +
              "approving an empty checklist halts the job rather than executing a fabricated plan."
            : !string.IsNullOrWhiteSpace(agentReasoning)
                ? agentReasoning!
                : string.IsNullOrWhiteSpace(input.Feedback)
                    ? $"Generated execution decomposition into {tasks.Count} sequential tasks based on initial prompt."
                    : $"Revised execution plan addressing operator feedback: {input.Feedback}";

        var payload = new PlanApprovalPayload(tasks, reasoning);
        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);

        var card = new JobTimelineCard
        {
            Id = Guid.NewGuid(),
            JobId = input.JobId,
            CardType = TimelineCardType.PlanApproval,
            Status = TimelineCardStatus.PendingInput,
            Title = noChecklist
                ? "Plan Approval: No Checklist Registered"
                : string.IsNullOrWhiteSpace(input.Feedback)
                    ? "Plan Approval: Proposed Implementation Checklist"
                    : "Plan Approval: Revised Implementation Checklist (Post-Feedback)",
            StructuredPayloadJson = payloadJson,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        dbContext.JobTimelineCards.Add(card);

        // One ask per decision: a plan hand-off posted with requires_reply during the discovery
        // turn (older prompts told drivers to do exactly that; bee job 67fc1b8a, 2026-11-20 left
        // the operator an "Escalation from @bee" NEEDS YOU card and a Pending Approval gate for
        // the same four tasks) must not sit open beside this gate. Two PendingInput cards means
        // the operator answers whichever they notice first and the job carries a wait nobody will
        // ever reply to - the same supersession rule the workflow applies to stale gate cards
        // inside RunDecompositionAndPlanApprovalAsync. Persona-addressed consultations are left
        // alone; no human is blocked on them.
        var supersededEscalations = new List<JobTimelineCard>();
        var openEscalations = await dbContext.JobTimelineCards
            .Where(c => c.JobId == input.JobId
                        && c.CardType == TimelineCardType.Escalation3R
                        && c.Status == TimelineCardStatus.PendingInput
                        && c.Id != card.Id)
            .ToListAsync(ct);
        foreach (var escalation in openEscalations.Where(EscalationTargetsOperator))
        {
            escalation.Status = TimelineCardStatus.Resolved;
            escalation.UpdatedAt = DateTimeOffset.UtcNow;
            escalation.StructuredPayloadJson = WithResolutionNote(
                escalation.StructuredPayloadJson,
                "Superseded by the Plan Approval gate: the plan is approved or rejected on that card.");
            supersededEscalations.Add(escalation);
        }

        if (job != null)
        {
            SyncJobTasksJson(job, tasks);
            job.ProgressHeartbeat = noChecklist
                ? "Awaiting plan approval — no checklist registered"
                : "Awaiting plan approval";
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await dbContext.SaveChangesAsync(ct);
        await NotifyCardUpdatedAsync(card);
        foreach (var stale in supersededEscalations)
        {
            await NotifyCardUpdatedAsync(stale);
            logger.LogInformation(
                "PlanApproval gate {GateCardId} superseded open operator escalation {EscalationCardId} " +
                "on job {JobId}; the gate is the one approval surface",
                card.Id, stale.Id, input.JobId);
        }

        logger.LogInformation("Created PlanApproval card {CardId} for job {JobId}", card.Id, input.JobId);
        if (noChecklist)
        {
            // The incident this guards against was silent end to end, so the absence is logged loudly: it is
            // the only place a driver that cannot reach manage_task shows up as anything at all.
            logger.LogWarning(
                "PlanApproval card {CardId} for job {JobId} carries an empty checklist: no " +
                "manage_task(set_checklist) call reached the platform during decomposition. The workflow halts " +
                "rather than inventing tasks, so expect the job to stop at the plan gate.",
                card.Id, input.JobId);
        }

        return new DecompositionActivityResult(card.Id, payload);
    }

    [Activity]
    public async Task<Guid> RecordTurnInterruptedAsync(TurnInterruptedCardInput input)
    {
        var payload = new
        {
            reason = input.Reason,
            requestedBy = input.RequestedBy,
            lastOutput = input.LastOutput,
            interruptedAt = DateTimeOffset.UtcNow,
            availableActions = new[] { "resume", "retry_step", "realign" }
        };
        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);

        var card = new JobTimelineCard
        {
            Id = Guid.NewGuid(),
            JobId = input.JobId,
            TaskId = input.TaskId,
            CardType = TimelineCardType.TurnInterrupted,
            Status = TimelineCardStatus.Active,
            Title = $"Turn Interrupted: {input.Reason}",
            StructuredPayloadJson = payloadJson,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        dbContext.JobTimelineCards.Add(card);

        var job = await TryLoadJobAsync(input.JobId);
        if (job != null)
        {
            job.ProgressHeartbeat = $"Turn interrupted: {input.Reason}";
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await dbContext.SaveChangesAsync();
        await NotifyCardUpdatedAsync(card);

        logger.LogInformation("Recorded TurnInterrupted card {CardId} for job {JobId} task {TaskId}", 
            card.Id, input.JobId, input.TaskId);

        return card.Id;
    }

    [Activity]
    public async Task PersistJobHaltAsync(PersistJobHaltInput input)
    {
        var job = await TryLoadJobAsync(input.JobId);
        if (job is null)
        {
            logger.LogWarning("PersistJobHalt: job {JobId} not found; halt '{Reason}' not written.",
                input.JobId, input.Reason);
            return;
        }

        // A job the operator already finished or archived is not "halted" — writing over its terminal
        // state would turn a deliberate close into a red Blocked badge, which is a worse lie than the
        // race it guards against (the complete/archive path stamps its own truth on the row).
        if (job.Status is ProjectJobStatus.Completed or ProjectJobStatus.Archived)
        {
            logger.LogInformation(
                "PersistJobHalt: job {JobId} is already {Status}; not overwriting with halt '{Reason}'.",
                job.Id, job.Status, input.Reason);
            return;
        }

        job.ProgressHeartbeat = input.Reason.StartsWith("Halted", StringComparison.OrdinalIgnoreCase)
            ? input.Reason
            : $"Halted: {input.Reason}";
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync();

        logger.LogInformation("Persisted halt for job {JobId}: {Heartbeat}", job.Id, job.ProgressHeartbeat);
    }

    [Activity]
    public async Task<Guid> UpdateTaskStepCardAsync(TaskStepCardInput input)
    {
        var existing = await dbContext.JobTimelineCards
            .FirstOrDefaultAsync(c => c.JobId == input.JobId && c.TaskId == input.TaskId && c.CardType == TimelineCardType.TaskStep);

        var payload = new
        {
            taskIndex = input.TaskIndex,
            taskTitle = input.TaskTitle,
            heartbeat = input.Heartbeat,
            stepDetails = input.StepDetails,
            updatedAt = DateTimeOffset.UtcNow
        };
        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);

        JobTimelineCard targetCard;
        if (existing != null)
        {
            existing.Status = input.Status;
            existing.Title = $"Task {input.TaskIndex + 1}: {input.TaskTitle}";
            existing.StructuredPayloadJson = payloadJson;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            targetCard = existing;
        }
        else
        {
            targetCard = new JobTimelineCard
            {
                Id = Guid.NewGuid(),
                JobId = input.JobId,
                TaskId = input.TaskId,
                CardType = TimelineCardType.TaskStep,
                Status = input.Status,
                Title = $"Task {input.TaskIndex + 1}: {input.TaskTitle}",
                StructuredPayloadJson = payloadJson,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            dbContext.JobTimelineCards.Add(targetCard);
        }

        var job = await TryLoadJobAsync(input.JobId);
        if (job != null)
        {
            job.CurrentTaskId = input.TaskId;
            job.ProgressHeartbeat = !string.IsNullOrWhiteSpace(input.Heartbeat)
                ? input.Heartbeat
                : $"Task {input.TaskIndex + 1}: {input.TaskTitle}";
            UpdateJobTaskStatusInJson(job, input.TaskIndex, input.TaskTitle, input.Status);
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await dbContext.SaveChangesAsync();
        await NotifyCardUpdatedAsync(targetCard);

        logger.LogInformation("Updated TaskStep card {CardId} for job {JobId} task {TaskId} ({Status})", 
            targetCard.Id, input.JobId, input.TaskId, input.Status);

        return targetCard.Id;
    }

    /// <summary>
    /// The structured questions one discovery turn actually asked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads the <c>ask_question</c> payload and nothing else. This method used to mine prose as well —
    /// numbered lists, any line ending in a question mark, "X vs Y" bullets — and that is the failure
    /// mode that costs the most: a card built from text an agent was thinking out loud in makes the
    /// operator answer a question nobody asked, which steers the job somewhere it was never headed. A
    /// discovery turn that did not ask has no questions, and saying so is the honest result.
    /// </para>
    /// <para>
    /// Both sanctioned channels are structured. Function-tool drivers call <c>ask_question</c>, whose
    /// arguments the platform records as tool-call args. Subprocess drivers (<c>claude -p</c>, <c>agy</c>,
    /// <c>copilot</c>) have no tool schemas at all, so they emit the same object as a fenced
    /// <c>ask_question</c> block, which reaches here as text.
    /// </para>
    /// <para>
    /// Which of the accepted wire shapes each driver produces is <see cref="AskQuestionPayload"/>'s
    /// business, not this method's. Insisting on the array form here is what made a real Antigravity
    /// question invisible: <c>{"questions":"[...]"}</c> parsed cleanly, held no array under
    /// <c>questions</c>, and came back as "no questions" — indistinguishable from an agent that had
    /// nothing to ask.
    /// </para>
    /// </remarks>
    internal static List<DiscoveryQuestion> ExtractQuestionsFromText(string text)
    {
        var result = new List<DiscoveryQuestion>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var jsonBlockMatch = Regex.Match(text, @"```(?:json:)?ask_question\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
        var trimmed = text.Trim();
        var jsonCandidate = jsonBlockMatch.Success
            ? jsonBlockMatch.Groups[1].Value.Trim()
            : trimmed.StartsWith('{') || trimmed.StartsWith('[') ? trimmed : null;

        if (!string.IsNullOrWhiteSpace(jsonCandidate))
        {
            int qIdx = 1;
            foreach (var question in AskQuestionPayload.Parse(jsonCandidate))
            {
                result.Add(new DiscoveryQuestion(
                    $"q_{qIdx++}",
                    question.Question,
                    question.Options.Count > 0 ? question.Options : null));
            }
        }

        // Nothing is inferred past this point. A driver that asked in prose rather than through the
        // channel its stage contract names is a contract violation to fix in the contract; the activity
        // reports an empty result loudly instead of guessing over the transcript to fill the gap.
        return result;
    }


    private static List<ProposedChecklistItem> ParseTasksFromJobJson(string? tasksJson)
    {
        var result = new List<ProposedChecklistItem>();
        if (string.IsNullOrWhiteSpace(tasksJson))
        {
            return result;
        }

        try
        {
            var dtos = JsonSerializer.Deserialize<List<JobTaskDto>>(tasksJson, JsonOptions);
            if (dtos == null) return result;

            for (int i = 0; i < dtos.Count; i++)
            {
                var dto = dtos[i];
                if (string.IsNullOrWhiteSpace(dto.Title)) continue;
                result.Add(new ProposedChecklistItem(
                    TaskId: dto.Id != Guid.Empty ? dto.Id.ToString() : $"task-{i + 1}",
                    Title: dto.Title,
                    TargetFile: !string.IsNullOrWhiteSpace(dto.TargetFile) ? dto.TargetFile! : "workspace",
                    VerificationMethod: !string.IsNullOrWhiteSpace(dto.VerificationMethod)
                        ? dto.VerificationMethod!
                        : (!string.IsNullOrWhiteSpace(dto.AcceptanceCriteria) ? dto.AcceptanceCriteria! : "Automated test verification")
                ));
            }
        }
        catch
        {
            // Ignore malformed JSON
        }

        return result;
    }

    private static void SyncJobTasksJson(ProjectJob job, List<ProposedChecklistItem> proposed)
    {
        var existing = new List<JobTaskDto>();
        try
        {
            if (!string.IsNullOrWhiteSpace(job.TasksJson))
            {
                existing = JsonSerializer.Deserialize<List<JobTaskDto>>(job.TasksJson, JsonOptions) ?? new();
            }
        }
        catch
        {
            existing = new();
        }

        if (existing.Count == proposed.Count && existing.Count > 0)
        {
            return;
        }

        var synced = proposed.Select(p => new JobTaskDto
        {
            Id = Guid.TryParse(p.TaskId, out var g) ? g : Guid.NewGuid(),
            Title = p.Title,
            TargetFile = p.TargetFile,
            VerificationMethod = p.VerificationMethod,
            AcceptanceCriteria = p.VerificationMethod,
            Status = JobTaskStatus.Todo,
            IsCompleted = false
        }).ToList();

        job.TasksJson = JsonSerializer.Serialize(synced, JsonOptions);
    }

    private static void UpdateJobTaskStatusInJson(ProjectJob job, int taskIndex, string taskTitle, TimelineCardStatus cardStatus)
    {
        try
        {
            var tasks = JsonSerializer.Deserialize<List<JobTaskDto>>(
                string.IsNullOrWhiteSpace(job.TasksJson) ? "[]" : job.TasksJson,
                JsonOptions) ?? new();

            if (tasks.Count == 0) return;

            var match = (taskIndex >= 0 && taskIndex < tasks.Count)
                ? tasks[taskIndex]
                : tasks.FirstOrDefault(t => string.Equals(t.Title, taskTitle, StringComparison.OrdinalIgnoreCase));

            if (match == null) return;

            switch (cardStatus)
            {
                case TimelineCardStatus.Active:
                    match.Status = JobTaskStatus.InProgress;
                    match.IsCompleted = false;
                    break;
                case TimelineCardStatus.Resolved:
                case TimelineCardStatus.Approved:
                    match.Status = JobTaskStatus.Completed;
                    match.IsCompleted = true;
                    match.CompletedAt = DateTimeOffset.UtcNow;
                    break;
            }

            match.StatusChangedAt = DateTimeOffset.UtcNow;
            job.TasksJson = JsonSerializer.Serialize(tasks, JsonOptions);
        }
        catch
        {
            // Ignore malformed JSON
        }
    }

    [Activity]
    public async Task<IReadOnlyList<ProposedChecklistItem>> AppendRemediationTasksAsync(AppendRemediationTasksInput input)
    {
        if (input.Tasks.Count == 0)
        {
            return input.Tasks;
        }

        var job = await TryLoadJobAsync(input.JobId);
        if (job == null)
        {
            return input.Tasks;
        }

        List<JobTaskDto> existing;
        try
        {
            existing = JsonSerializer.Deserialize<List<JobTaskDto>>(
                string.IsNullOrWhiteSpace(job.TasksJson) ? "[]" : job.TasksJson,
                JsonOptions) ?? [];
        }
        catch
        {
            existing = [];
        }

        var resolvedItems = new List<ProposedChecklistItem>(input.Tasks.Count);
        foreach (var proposed in input.Tasks)
        {
            var id = Guid.TryParse(proposed.TaskId, out var parsed) && parsed != Guid.Empty
                ? parsed
                : Guid.NewGuid();

            existing.Add(new JobTaskDto
            {
                Id = id,
                Title = proposed.Title,
                TargetFile = proposed.TargetFile,
                VerificationMethod = proposed.VerificationMethod,
                AcceptanceCriteria = proposed.VerificationMethod,
                Status = JobTaskStatus.Todo,
                IsCompleted = false,
                StatusChangedAt = DateTimeOffset.UtcNow
            });

            resolvedItems.Add(proposed with { TaskId = id.ToString("D") });
        }

        job.TasksJson = JsonSerializer.Serialize(existing, JsonOptions);
        job.CurrentTaskId = resolvedItems[0].TaskId;
        job.ProgressHeartbeat = $"Round {input.Round} remediation ({input.Source}): {resolvedItems.Count} task(s) queued";
        job.UpdatedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync();
        logger.LogInformation(
            "Appended {Count} remediation checklist task(s) for job {JobId} (round {Round}, source {Source})",
            resolvedItems.Count, input.JobId, input.Round, input.Source);

        return resolvedItems;
    }
}

