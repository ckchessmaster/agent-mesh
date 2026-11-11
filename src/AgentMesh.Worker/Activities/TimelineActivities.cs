using System.Text.Json;
using AgentMesh.Core.Activities;
using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Temporalio.Activities;

namespace AgentMesh.Worker.Activities;

public class TimelineActivities(
    AgentMeshDbContext dbContext,
    ILogger<TimelineActivities> logger,
    IConfiguration? config = null,
    HttpClient? httpClient = null) : ITimelineActivities
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

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
        await dbContext.SaveChangesAsync();

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
            await dbContext.SaveChangesAsync();

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
        var questions = new List<DiscoveryQuestion>
        {
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
        };

        var payload = new DiscoveryPayload(questions, $"Discovery questionnaire for initial prompt: {input.InitialPrompt}");
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
        await dbContext.SaveChangesAsync();

        logger.LogInformation("Created DiscoveryQa card {CardId} for job {JobId}", card.Id, input.JobId);

        return new DiscoveryQaActivityResult(card.Id, payload);
    }

    [Activity]
    public async Task<DecompositionActivityResult> ExecuteDecompositionActivityAsync(DecompositionActivityInput input)
    {
        var tasks = new List<ProposedChecklistItem>
        {
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
        };

        var reasoning = string.IsNullOrWhiteSpace(input.Feedback)
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
        await dbContext.SaveChangesAsync();

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
        await dbContext.SaveChangesAsync();

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

        if (existing != null)
        {
            existing.Status = input.Status;
            existing.Title = $"Task {input.TaskIndex + 1}: {input.TaskTitle}";
            existing.StructuredPayloadJson = payloadJson;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync();
            return existing.Id;
        }

        var card = new JobTimelineCard
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

        dbContext.JobTimelineCards.Add(card);
        await dbContext.SaveChangesAsync();

        logger.LogInformation("Created TaskStep card {CardId} for job {JobId} task {TaskId}", 
            card.Id, input.JobId, input.TaskId);

        return card.Id;
    }
}
