using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMesh.Core.Activities;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
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
                card.StructuredPayloadJson = input.StructuredPayloadJson;
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

                // Also check if any ask_question tool call was logged in session messages
                var recentToolCallArgs = await (from sm in dbContext.SessionMessages.AsNoTracking()
                                                join s in dbContext.AgentSessions.AsNoTracking() on sm.SessionId equals s.Id
                                                where s.MetadataJson.Contains(job.Id.ToString()) && sm.ToolName == "ask_question"
                                                orderby sm.CreatedAt descending
                                                select sm.ToolArgsJson)
                                                .FirstOrDefaultAsync(ct);

                if (!string.IsNullOrWhiteSpace(recentToolCallArgs))
                {
                    questions = ExtractQuestionsFromText(recentToolCallArgs);
                }

                if (questions.Count == 0 && !string.IsNullOrWhiteSpace(recentMessage))
                {
                    questions = ExtractQuestionsFromText(recentMessage);
                }

                if (questions.Count == 0)
                {
                    var recentSessionResponse = await (from sm in dbContext.SessionMessages.AsNoTracking()
                                                       join s in dbContext.AgentSessions.AsNoTracking() on sm.SessionId equals s.Id
                                                       where s.MetadataJson.Contains(job.Id.ToString()) && sm.EventType == "response"
                                                       orderby sm.CreatedAt descending
                                                       select sm.Content)
                                                       .FirstOrDefaultAsync(ct);

                    if (!string.IsNullOrWhiteSpace(recentSessionResponse))
                    {
                        questions = ExtractQuestionsFromText(recentSessionResponse);
                        if (!string.IsNullOrWhiteSpace(recentSessionResponse) && (string.IsNullOrWhiteSpace(agentSummary) || agentSummary.StartsWith("Turn concluded")))
                        {
                            agentSummary = recentSessionResponse;
                        }
                    }
                }

                if (questions.Count == 0 && !string.IsNullOrWhiteSpace(outcome.Message))
                {
                    questions = ExtractQuestionsFromText(outcome.Message);
                }

                if (questions.Count == 0)
                {
                    var recentThoughts = await (from sm in dbContext.SessionMessages.AsNoTracking()
                                                join s in dbContext.AgentSessions.AsNoTracking() on sm.SessionId equals s.Id
                                                where s.MetadataJson.Contains(job.Id.ToString()) && sm.EventType == "thought"
                                                orderby sm.CreatedAt descending
                                                select sm.Content)
                                                .Take(10)
                                                .ToListAsync(ct);

                    foreach (var thought in recentThoughts)
                    {
                        if (string.IsNullOrWhiteSpace(thought)) continue;
                        var extracted = ExtractQuestionsFromText(thought);
                        if (extracted.Count > 0)
                        {
                            questions = extracted;
                            if (string.IsNullOrWhiteSpace(agentSummary) || agentSummary.StartsWith("Turn concluded"))
                            {
                                agentSummary = "Clarifying questions formulated during discovery exploration.";
                            }
                            break;
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
            logger.LogInformation("Discovery turn for job {JobId} concluded without clarifying questions; skipping DiscoveryQa card", input.JobId);
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
                logger.LogWarning(ex, "Agent decomposition turn encountered an error for job {JobId}; using fallback task breakdown", input.JobId);
            }
        }
        else if (job != null)
        {
            tasks = ParseTasksFromJobJson(job.TasksJson);
        }

        if (tasks.Count == 0)
        {
            tasks =
            [
                new(
                    "task-1",
                    $"Domain Models & Database Schema ({input.JobId})",
                    "src/AgentMesh.Core/Models",
                    "EF Core migration & database update"
                ),
                new(
                    "task-2",
                    $"Temporal Orchestration & Activities ({input.JobId})",
                    "src/AgentMesh.Worker/Workflows",
                    "Temporal unit tests & determinism checks"
                ),
                new(
                    "task-3",
                    $"API Endpoints & SignalR Broadcasts ({input.JobId})",
                    "src/AgentMesh.Api/Controllers",
                    "REST endpoint integration tests"
                ),
                new(
                    "task-4",
                    $"Cockpit Dashboard & Stateful Cards ({input.JobId})",
                    "src/AgentMesh.Dashboard",
                    "Vitest component specs & build validation"
                )
            ];
        }

        var reasoning = !string.IsNullOrWhiteSpace(agentReasoning)
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
            Title = string.IsNullOrWhiteSpace(input.Feedback) 
                ? "Plan Approval: Proposed Implementation Checklist"
                : "Plan Approval: Revised Implementation Checklist (Post-Feedback)",
            StructuredPayloadJson = payloadJson,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        dbContext.JobTimelineCards.Add(card);

        if (job != null)
        {
            SyncJobTasksJson(job, tasks);
            job.ProgressHeartbeat = "Awaiting plan approval";
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await dbContext.SaveChangesAsync(ct);
        await NotifyCardUpdatedAsync(card);

        logger.LogInformation("Created PlanApproval card {CardId} for job {JobId}", card.Id, input.JobId);

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

    internal static List<DiscoveryQuestion> ExtractQuestionsFromText(string text)
    {
        var result = new List<DiscoveryQuestion>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        // 1. Check for ```ask_question or ```json:ask_question blocks or raw json with "questions"
        var jsonBlockMatch = Regex.Match(text, @"```(?:json:)?ask_question\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
        var jsonCandidate = jsonBlockMatch.Success ? jsonBlockMatch.Groups[1].Value.Trim() : (text.Trim().StartsWith('{') ? text.Trim() : null);

        if (!string.IsNullOrWhiteSpace(jsonCandidate))
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonCandidate);
                if (doc.RootElement.TryGetProperty("questions", out var qArray) && qArray.ValueKind == JsonValueKind.Array)
                {
                    int qIdx = 1;
                    foreach (var qElem in qArray.EnumerateArray())
                    {
                        var qText = qElem.TryGetProperty("question", out var qt) ? qt.GetString() : null;
                        if (string.IsNullOrWhiteSpace(qText) && qElem.TryGetProperty("id", out var idProp))
                        {
                            qText = idProp.GetString();
                        }

                        if (!string.IsNullOrWhiteSpace(qText))
                        {
                            var opts = new List<string>();
                            if (qElem.TryGetProperty("options", out var opArr) && opArr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var o in opArr.EnumerateArray())
                                {
                                    var optStr = o.GetString();
                                    if (!string.IsNullOrWhiteSpace(optStr)) opts.Add(optStr);
                                }
                            }
                            result.Add(new DiscoveryQuestion($"q_{qIdx++}", qText.Trim(), opts.Count > 0 ? opts : null));
                            if (result.Count >= 5) return result;
                        }
                    }
                    if (result.Count > 0) return result;
                }
            }
            catch { }
        }

        // 2. Check for numbered or bulleted questions or domain tradeoffs:
        // e.g. "1. Question?" or "- Question?" or "- Storage: table vs metadata" or "- Q2: Who answers?"
        var numberedMatches = Regex.Matches(text, @"^\s*(?:\d+[\.\)]|[-*•])\s+(.+)$", RegexOptions.Multiline);
        if (numberedMatches.Count > 0)
        {
            int qIdx = 1;
            foreach (Match m in numberedMatches)
            {
                var line = m.Groups[1].Value.Trim();
                var isQuestionCandidate = line.Contains('?') ||
                    Regex.IsMatch(line, @"\bvs(?:\.|\b)", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(line, @"^Q\d+[:\s]", RegexOptions.IgnoreCase);

                if (isQuestionCandidate && line.Length > 8)
                {
                    if (line.StartsWith("Questions to ask", StringComparison.OrdinalIgnoreCase)) continue;

                    var extractedOpts = new List<string>();
                    if (Regex.IsMatch(line, @"\bvs(?:\.|\b)", RegexOptions.IgnoreCase))
                    {
                        var vsParts = Regex.Split(line, @"\s*\bvs(?:\.|\b)\s*", RegexOptions.IgnoreCase);
                        if (vsParts.Length >= 2)
                        {
                            var optA = CleanVsOption(vsParts[0]);
                            var optB = CleanVsOption(vsParts[1]);
                            if (!string.IsNullOrWhiteSpace(optA) && !string.IsNullOrWhiteSpace(optB))
                            {
                                extractedOpts.Add(optA);
                                extractedOpts.Add(optB);
                            }
                        }
                    }

                    if (extractedOpts.Count == 0)
                    {
                        var parenMatch = Regex.Match(line, @"\(([^)]+)\)");
                        if (parenMatch.Success)
                        {
                            var rawChoices = Regex.Split(parenMatch.Groups[1].Value, @"[,/]| or ", RegexOptions.IgnoreCase);
                            foreach (var c in rawChoices)
                            {
                                var trimmed = Regex.Replace(c.Trim(), @"^(?:etc|e\.g\.)\.?$", "", RegexOptions.IgnoreCase).Trim();
                                if (trimmed.Length > 1 && trimmed.Length < 35 && !extractedOpts.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                                {
                                    extractedOpts.Add(trimmed);
                                }
                            }
                        }
                    }

                    var cleaned = Regex.Replace(line, @"\s*\([^)]*\)", "").Trim().TrimStart('-', '*', '•', '#', ' ').Trim();
                    result.Add(new DiscoveryQuestion($"q_{qIdx++}", cleaned, extractedOpts.Count > 0 ? extractedOpts : null));
                    if (result.Count >= 5) break;
                }
            }
            if (result.Count > 0) return result;
        }

        // 3. Line-by-line fallback for lines ending with '?'
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int idx = 1;
        foreach (var line in lines)
        {
            if (line.EndsWith('?') && line.Length > 10)
            {
                var cleaned = line.TrimStart('-', '*', '•', '#', ' ').Trim();
                result.Add(new DiscoveryQuestion($"q_{idx++}", cleaned));
                if (result.Count >= 5) break;
            }
        }
        return result;
    }

    private static string? CleanVsOption(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim().TrimStart('-', '*', '•', '.', ' ');

        // 1. Strip label / prefix before colon if short, e.g. "Storage: ..." or "Q1: ..."
        var colonIdx = text.IndexOf(':');
        if (colonIdx >= 0 && colonIdx < text.Length - 1 && colonIdx < 25)
        {
            text = text[(colonIdx + 1)..].Trim();
        }

        // 2. Strip prefix before " - ", e.g. "Read-only enforcement depth - hard refusal..."
        var dashIdx = text.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx >= 0 && dashIdx < text.Length - 3)
        {
            text = text[(dashIdx + 3)..].Trim();
        }

        // 3. Remove parentheticals before period or plus checks, e.g. "(migration + Architect)" or "(advisory)"
        text = Regex.Replace(text, @"\s*\([^)]*\)", "").Trim();

        // 4. Strip sentence continuation after period, e.g. "... + metadata. My default: reuse..."
        var periodIdx = text.IndexOf('.');
        if (periodIdx >= 0)
        {
            text = text[..periodIdx].Trim();
        }

        // 5. Strip trailing additions with '+', e.g. "... + metadata"
        var plusIdx = text.IndexOf('+');
        if (plusIdx > 0)
        {
            text = text[..plusIdx].Trim();
        }

        text = text.Trim();
        if (text.Length >= 2 &&
            ((text.StartsWith('\'') && text.EndsWith('\'')) ||
             (text.StartsWith('"') && text.EndsWith('"')) ||
             (text.StartsWith('`') && text.EndsWith('`'))))
        {
            text = text[1..^1].Trim();
        }

        if (text.Length > 60) text = text[..60].Trim();
        return text.Length >= 2 ? text : null;
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

