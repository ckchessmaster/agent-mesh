using System.Text.Json;
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
                    job.ProgressHeartbeat = "Completed all tasks successfully";
                }
                job.UpdatedAt = DateTimeOffset.UtcNow;
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

                var recentMessage = await dbContext.JobMessages
                    .AsNoTracking()
                    .Where(m => m.JobId == job.Id && m.SenderType == "agent")
                    .OrderByDescending(m => m.CreatedAt)
                    .Select(m => m.Content)
                    .FirstOrDefaultAsync(ct);

                var sourceText = !string.IsNullOrWhiteSpace(recentMessage) ? recentMessage : outcome.Message;
                if (!string.IsNullOrWhiteSpace(sourceText))
                {
                    questions = ExtractQuestionsFromText(sourceText!);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Agent discovery turn encountered an error for job {JobId}; using structured default questions", input.JobId);
            }
        }

        if (questions.Count == 0)
        {
            questions =
            [
                new(
                    "env_target",
                    "What is the target runtime environment for this feature?",
                    new[] { "Local Development (Docker/Bare Metal)", "Staging Cluster", "Production Workload" }
                ),
                new(
                    "data_preservation",
                    "Should existing database entities and migration states be preserved?",
                    new[] { "Strict zero-loss data preservation", "Destructive resets permitted" }
                ),
                new(
                    "validation_mode",
                    "Which test execution mode should be enforced for verification?",
                    new[] { "Full integration test suite (dotnet + vitest)", "Unit tests only", "Dry-run verification" }
                )
            ];
        }

        var summary = !string.IsNullOrWhiteSpace(agentSummary)
            ? agentSummary!
            : $"Discovery questionnaire for initial prompt: {input.InitialPrompt}";

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

        logger.LogInformation("Created DiscoveryQa card {CardId} for job {JobId}", card.Id, input.JobId);

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

    private static List<DiscoveryQuestion> ExtractQuestionsFromText(string text)
    {
        var result = new List<DiscoveryQuestion>();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int idx = 1;
        foreach (var line in lines)
        {
            if (line.EndsWith('?') && line.Length > 10)
            {
                var cleaned = line.TrimStart('-', '*', '•', '#', ' ').Trim();
                result.Add(new DiscoveryQuestion($"q_{idx}", cleaned));
                idx++;
                if (result.Count >= 5) break;
            }
        }
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
}

