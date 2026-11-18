using System.Text.Json;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Controllers;

public record DashboardJobStatsDto(
    int TotalProjects,
    int ActiveProjects,
    int TotalJobs,
    int ActiveJobs,
    int PendingJobs,
    int CompletedJobs,
    int ArchivedJobs,
    int AttentionCount,
    int ActiveSessions,
    int ThinkingSessions,
    int WaitingForInputSessions
);

public record DashboardAttentionItemDto(
    string Id,
    string Kind,
    string Severity,
    string Title,
    string? Summary,
    string? ProjectId,
    string? ProjectName,
    Guid? JobId,
    string? JobTitle,
    Guid? CardId,
    string? CardType,
    Guid? SessionId,
    string? PersonaId,
    string? PersonaName,
    string? DriverId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record DashboardOverviewDto(
    DateTimeOffset GeneratedAt,
    DashboardJobStatsDto JobStats,
    List<ProjectJobDto> RecentJobs,
    List<DashboardAttentionItemDto> AttentionItems
);

[ApiController]
[Route("api/dashboard")]
[Authorize]
public class DashboardController(AgentMeshDbContext db) : ControllerBase
{
    public const int DefaultRecentJobsLimit = 20;
    public const int DefaultAttentionLimit = 50;
    public const int MaxItemsLimit = 100;

    internal static readonly string[] LiveSessionStatuses =
        ["active", "thinking", "busy", "waiting_for_input"];

    [HttpGet("overview")]
    [AllowAnonymous]
    public async Task<IActionResult> GetOverview(
        [FromQuery] int recentJobsLimit = DefaultRecentJobsLimit,
        [FromQuery] int attentionLimit = DefaultAttentionLimit,
        [FromQuery] bool includeArchived = true,
        CancellationToken ct = default)
    {
        var boundedRecentLimit = Math.Clamp(recentJobsLimit, 1, MaxItemsLimit);
        var boundedAttentionLimit = Math.Clamp(attentionLimit, 1, MaxItemsLimit);

        var projectsById = await db.Projects
            .AsNoTracking()
            .Select(p => new { p.Id, p.Name })
            .ToDictionaryAsync(p => p.Id, p => p.Name, StringComparer.OrdinalIgnoreCase, ct);

        var personasById = await db.Personas
            .AsNoTracking()
            .Select(p => new { p.Id, p.Name })
            .ToDictionaryAsync(p => p.Id, p => p.Name, StringComparer.OrdinalIgnoreCase, ct);

        var jobStatusRows = await db.ProjectJobs
            .AsNoTracking()
            .Select(j => new { j.Id, j.ProjectId, j.Status })
            .ToListAsync(ct);

        var totalJobs = jobStatusRows.Count;
        var activeJobs = 0;
        var pendingJobs = 0;
        var completedJobs = 0;
        var archivedJobs = 0;
        var activeProjectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var finishedJobIds = new HashSet<Guid>();

        foreach (var row in jobStatusRows)
        {
            switch (row.Status)
            {
                case ProjectJobStatus.Active:
                    activeJobs++;
                    if (!string.IsNullOrWhiteSpace(row.ProjectId))
                    {
                        activeProjectIds.Add(row.ProjectId);
                    }
                    break;
                case ProjectJobStatus.Pending:
                    pendingJobs++;
                    break;
                case ProjectJobStatus.Completed:
                    completedJobs++;
                    finishedJobIds.Add(row.Id);
                    break;
                case ProjectJobStatus.Archived:
                    archivedJobs++;
                    finishedJobIds.Add(row.Id);
                    break;
            }
        }

        var recentJobsQuery = BuildRecentJobsQuery(db, includeArchived, boundedRecentLimit);
        var recentJobEntities = await recentJobsQuery.ToListAsync(ct);

        var recentJobIdStrings = recentJobEntities.Select(j => j.Id.ToString()).ToList();
        var cardCounts = recentJobIdStrings.Count == 0
            ? new Dictionary<string, int>()
            : await db.JobTimelineCards
                .AsNoTracking()
                .Where(c => recentJobIdStrings.Contains(c.JobId))
                .GroupBy(c => c.JobId)
                .Select(g => new { JobId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.JobId, x => x.Count, ct);

        var recentJobs = recentJobEntities
            .Select(j => ProjectJobsController.ToDto(
                j,
                projectsById.GetValueOrDefault(j.ProjectId),
                cardCounts.GetValueOrDefault(j.Id.ToString(), 0)))
            .ToList();

        var liveJobs = await BuildLiveJobsQuery(db).ToListAsync(ct);
        var liveJobsById = liveJobs.ToDictionary(j => j.Id);

        var actionableCards = await BuildActionableCardsQuery(db).ToListAsync(ct);
        var liveSessions = await BuildLiveSessionsQuery(db).ToListAsync(ct);

        var thinkingSessions = liveSessions.Count(s =>
            string.Equals(s.Status, "thinking", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Status, "busy", StringComparison.OrdinalIgnoreCase));
        var waitingForInputSessions = liveSessions.Count(s =>
            string.Equals(s.Status, "waiting_for_input", StringComparison.OrdinalIgnoreCase));
        var activeSessions = liveSessions.Count;

        var allAttentionItems = new List<DashboardAttentionItemDto>();
        var jobsWithCardAttention = new HashSet<Guid>();
        var jobsWithSessionAttention = new HashSet<Guid>();

        // 1. Timeline cards waiting for operator input or intervention on live jobs
        foreach (var card in actionableCards)
        {
            if (!Guid.TryParse(card.JobId, out var cardJobId))
            {
                continue;
            }

            if (!liveJobsById.TryGetValue(cardJobId, out var job))
            {
                continue;
            }

            jobsWithCardAttention.Add(job.Id);

            var (kind, severity) = ClassifyTimelineCard(card);
            var (payloadSummary, payloadPersonaId, payloadDriverId) = ParseCardPayloadMetadata(card.StructuredPayloadJson);
            var personaId = FirstNonBlank(payloadPersonaId, job.AssignedAgentId);
            var personaName = ResolveName(personaId, personasById);
            var projectName = projectsById.GetValueOrDefault(job.ProjectId);
            var summary = TruncateSummary(FirstNonBlank(payloadSummary, job.ProgressHeartbeat, job.Title));

            allAttentionItems.Add(new DashboardAttentionItemDto(
                Id: $"card:{card.Id}",
                Kind: kind,
                Severity: severity,
                Title: !string.IsNullOrWhiteSpace(card.Title) ? card.Title : job.Title,
                Summary: summary,
                ProjectId: job.ProjectId,
                ProjectName: projectName,
                JobId: job.Id,
                JobTitle: job.Title,
                CardId: card.Id,
                CardType: card.CardType.ToString(),
                SessionId: null,
                PersonaId: personaId,
                PersonaName: personaName,
                DriverId: payloadDriverId,
                CreatedAt: card.CreatedAt,
                UpdatedAt: card.UpdatedAt
            ));
        }

        // 2. Agent sessions parked in waiting_for_input (deduplicated if the parent job already has an open card)
        foreach (var session in liveSessions.Where(s =>
                     string.Equals(s.Status, "waiting_for_input", StringComparison.OrdinalIgnoreCase)))
        {
            var meta = ParseSessionMetadata(session.MetadataJson);
            ProjectJob? sessionJob = null;

            if (meta.JobId.HasValue)
            {
                if (finishedJobIds.Contains(meta.JobId.Value) || jobsWithCardAttention.Contains(meta.JobId.Value))
                {
                    continue;
                }

                if (liveJobsById.TryGetValue(meta.JobId.Value, out sessionJob))
                {
                    jobsWithSessionAttention.Add(sessionJob.Id);
                }
            }

            var personaId = FirstNonBlank(session.PersonaId, meta.PersonaId, sessionJob?.AssignedAgentId);
            var personaName = ResolveName(personaId, personasById);
            var driverId = FirstNonBlank(session.DriverId, session.CreatorAgentId);
            var projectId = FirstNonBlank(sessionJob?.ProjectId, meta.ProjectId);
            var projectName = (projectId != null ? projectsById.GetValueOrDefault(projectId) : null) ?? meta.ProjectName;
            var jobTitle = FirstNonBlank(sessionJob?.Title, meta.JobTitle);
            var title = !string.IsNullOrWhiteSpace(session.Title)
                ? session.Title.Trim()
                : personaName != null
                    ? $"Session with @{personaName}"
                    : "Interactive session";
            var summary = TruncateSummary(FirstNonBlank(sessionJob?.ProgressHeartbeat, "Waiting for operator input"));

            allAttentionItems.Add(new DashboardAttentionItemDto(
                Id: $"session:{session.Id}",
                Kind: "session_waiting_input",
                Severity: "warning",
                Title: title,
                Summary: summary,
                ProjectId: projectId,
                ProjectName: projectName,
                JobId: sessionJob?.Id ?? meta.JobId,
                JobTitle: jobTitle,
                CardId: null,
                CardType: null,
                SessionId: session.Id,
                PersonaId: personaId,
                PersonaName: personaName,
                DriverId: driverId,
                CreatedAt: session.CreatedAt,
                UpdatedAt: session.UpdatedAt
            ));
        }

        // 3. Active jobs in blocked / awaiting lifecycle states without a corresponding card or waiting session
        foreach (var job in liveJobs)
        {
            if (job.Status != ProjectJobStatus.Active ||
                jobsWithCardAttention.Contains(job.Id) ||
                jobsWithSessionAttention.Contains(job.Id))
            {
                continue;
            }

            var jobClassification = ClassifyJobAttention(job);
            if (jobClassification == null)
            {
                continue;
            }

            var (kind, severity, summaryText) = jobClassification.Value;
            var personaId = FirstNonBlank(job.AssignedAgentId);
            var personaName = ResolveName(personaId, personasById);
            var projectName = projectsById.GetValueOrDefault(job.ProjectId);

            allAttentionItems.Add(new DashboardAttentionItemDto(
                Id: $"job:{job.Id}",
                Kind: kind,
                Severity: severity,
                Title: job.Title,
                Summary: TruncateSummary(summaryText),
                ProjectId: job.ProjectId,
                ProjectName: projectName,
                JobId: job.Id,
                JobTitle: job.Title,
                CardId: null,
                CardType: null,
                SessionId: null,
                PersonaId: personaId,
                PersonaName: personaName,
                DriverId: null,
                CreatedAt: job.CreatedAt,
                UpdatedAt: job.UpdatedAt
            ));
        }

        var orderedAttention = allAttentionItems
            .OrderBy(i => SeverityRank(i.Severity))
            .ThenByDescending(i => i.UpdatedAt)
            .ThenByDescending(i => i.CreatedAt)
            .ToList();

        var totalAttentionCount = orderedAttention.Count;
        var boundedAttention = orderedAttention.Take(boundedAttentionLimit).ToList();

        var stats = new DashboardJobStatsDto(
            TotalProjects: projectsById.Count,
            ActiveProjects: activeProjectIds.Count,
            TotalJobs: totalJobs,
            ActiveJobs: activeJobs,
            PendingJobs: pendingJobs,
            CompletedJobs: completedJobs,
            ArchivedJobs: archivedJobs,
            AttentionCount: totalAttentionCount,
            ActiveSessions: activeSessions,
            ThinkingSessions: thinkingSessions,
            WaitingForInputSessions: waitingForInputSessions
        );

        return Ok(new DashboardOverviewDto(
            GeneratedAt: DateTimeOffset.UtcNow,
            JobStats: stats,
            RecentJobs: recentJobs,
            AttentionItems: boundedAttention
        ));
    }

    internal static IQueryable<ProjectJob> BuildRecentJobsQuery(
        AgentMeshDbContext db,
        bool includeArchived,
        int limit)
    {
        var query = db.ProjectJobs.AsNoTracking();
        if (!includeArchived)
        {
            query = query.Where(j => j.Status != ProjectJobStatus.Archived);
        }

        return query
            .OrderByDescending(j => j.UpdatedAt)
            .ThenByDescending(j => j.CreatedAt)
            .Take(limit)
            .Select(j => new ProjectJob
            {
                Id = j.Id,
                ProjectId = j.ProjectId,
                Title = j.Title,
                Description = j.Description,
                Status = j.Status,
                Priority = j.Priority,
                GitBranchName = j.GitBranchName,
                S3ArchiveKey = j.S3ArchiveKey,
                TasksJson = j.TasksJson,
                QueuedTurnsJson = j.QueuedTurnsJson,
                ResultSummary = j.ResultSummary,
                ApprovalPolicy = j.ApprovalPolicy,
                CurrentTaskId = j.CurrentTaskId,
                ProgressHeartbeat = j.ProgressHeartbeat,
                AssignedAgentId = j.AssignedAgentId,
                CreatedAt = j.CreatedAt,
                UpdatedAt = j.UpdatedAt
            });
    }

    internal static IQueryable<ProjectJob> BuildLiveJobsQuery(AgentMeshDbContext db) =>
        db.ProjectJobs
            .AsNoTracking()
            .Where(j => j.Status == ProjectJobStatus.Active || j.Status == ProjectJobStatus.Pending)
            .Select(j => new ProjectJob
            {
                Id = j.Id,
                ProjectId = j.ProjectId,
                Title = j.Title,
                Status = j.Status,
                Priority = j.Priority,
                TasksJson = j.TasksJson,
                ApprovalPolicy = j.ApprovalPolicy,
                CurrentTaskId = j.CurrentTaskId,
                ProgressHeartbeat = j.ProgressHeartbeat,
                AssignedAgentId = j.AssignedAgentId,
                CreatedAt = j.CreatedAt,
                UpdatedAt = j.UpdatedAt
            });

    internal static IQueryable<JobTimelineCard> BuildActionableCardsQuery(AgentMeshDbContext db) =>
        db.JobTimelineCards
            .AsNoTracking()
            .Where(c => c.Status == TimelineCardStatus.PendingInput ||
                        (c.CardType == TimelineCardType.TurnInterrupted && c.Status == TimelineCardStatus.Active))
            .OrderByDescending(c => c.UpdatedAt);

    internal record LiveSessionRow(
        Guid Id,
        string Title,
        string Status,
        string MetadataJson,
        string? PersonaId,
        string? DriverId,
        string? CreatorAgentId,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt
    );

    internal static IQueryable<LiveSessionRow> BuildLiveSessionsQuery(AgentMeshDbContext db) =>
        db.AgentSessions
            .AsNoTracking()
            .Where(s => LiveSessionStatuses.Contains(s.Status))
            .Select(s => new LiveSessionRow(
                s.Id,
                s.Title,
                s.Status,
                s.MetadataJson,
                s.PersonaId,
                s.DriverId,
                s.CreatorAgentId,
                s.CreatedAt,
                s.UpdatedAt
            ));

    private static (string Kind, string Severity) ClassifyTimelineCard(JobTimelineCard card) =>
        card.CardType switch
        {
            TimelineCardType.Escalation3R => ("escalation", "critical"),
            TimelineCardType.TurnInterrupted => ("turn_interrupted", "critical"),
            TimelineCardType.PlanApproval => ("plan_approval", "warning"),
            TimelineCardType.DiscoveryQa => ("discovery_qa", "warning"),
            TimelineCardType.PrReview => ("pr_review", "warning"),
            _ => ("pending_card", "warning")
        };

    private static (string Kind, string Severity, string Summary)? ClassifyJobAttention(ProjectJob job)
    {
        var lifecycle = ProjectJobsController.DeriveLifecycleStatus(job);
        var heartbeat = job.ProgressHeartbeat?.Trim() ?? string.Empty;

        if (lifecycle == "AwaitingApproval")
        {
            return ("job_awaiting_approval", "warning",
                !string.IsNullOrWhiteSpace(heartbeat) ? heartbeat : "Awaiting plan approval");
        }

        var tasks = ProjectJobsController.ParseTasks(job.TasksJson);
        if (lifecycle == "AwaitingReview" ||
            tasks.Any(t => !t.IsCompleted && string.Equals(t.Status, JobTaskStatus.AwaitingReview, StringComparison.OrdinalIgnoreCase)))
        {
            return ("job_awaiting_review", "warning",
                !string.IsNullOrWhiteSpace(heartbeat) ? heartbeat : "Awaiting review");
        }

        if (heartbeat.Contains("Blocked", StringComparison.OrdinalIgnoreCase) ||
            heartbeat.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
            heartbeat.Contains("Turn interrupted", StringComparison.OrdinalIgnoreCase) ||
            heartbeat.Contains("Escalated", StringComparison.OrdinalIgnoreCase) ||
            tasks.Any(t => !t.IsCompleted && string.Equals(t.Status, "blocked", StringComparison.OrdinalIgnoreCase)))
        {
            return ("job_blocked", "critical",
                !string.IsNullOrWhiteSpace(heartbeat) ? heartbeat : "Job is blocked and needs operator attention");
        }

        if (heartbeat.Contains("Awaiting", StringComparison.OrdinalIgnoreCase))
        {
            return ("job_awaiting_approval", "warning", heartbeat);
        }

        return null;
    }

    private static int SeverityRank(string severity) =>
        severity switch
        {
            "critical" => 0,
            "warning" => 1,
            _ => 2
        };

    private static (string? Summary, string? PersonaId, string? DriverId) ParseCardPayloadMetadata(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return (null, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null, null);
            }

            var root = doc.RootElement;
            var summary = TryReadString(root, "rawContent", "summary", "reasoning", "reason", "result", "content", "stepDetails");
            var personaId = TryReadString(root, "personaId", "senderPersona", "assignedPersonaId");
            var driverId = TryReadString(root, "driverId", "senderDriver");
            return (summary, personaId, driverId);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private readonly record struct SessionMetadata(
        Guid? JobId,
        string? JobTitle,
        string? ProjectId,
        string? ProjectName,
        string? PersonaId
    );

    private static SessionMetadata ParseSessionMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return default;
        }

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            var root = doc.RootElement;
            var jobIdRaw = TryReadString(root, "jobId", "JobId", "job_id");
            Guid? parsedJobId = Guid.TryParse(jobIdRaw, out var jid) ? jid : null;

            return new SessionMetadata(
                JobId: parsedJobId,
                JobTitle: TryReadString(root, "jobTitle", "JobTitle", "job_title"),
                ProjectId: TryReadString(root, "projectId", "ProjectId", "project_id"),
                ProjectName: TryReadString(root, "projectName", "ProjectName", "project_name"),
                PersonaId: TryReadString(root, "personaId", "PersonaId", "persona_id")
            );
        }
        catch
        {
            return default;
        }
    }

    private static string? TryReadString(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetProperty(key, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.String)
                {
                    var val = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        return val.Trim();
                    }
                }
                else if (prop.ValueKind == JsonValueKind.Number)
                {
                    return prop.GetRawText();
                }
            }
        }

        return null;
    }

    private static string? FirstNonBlank(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return null;
    }

    private static string? ResolveName(string? id, IReadOnlyDictionary<string, string> namesById)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return namesById.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : id;
    }

    private static string? TruncateSummary(string? text, int maxLength = 220)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var firstLine = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return null;
        }

        return firstLine.Length <= maxLength
            ? firstLine
            : string.Concat(firstLine.AsSpan(0, maxLength - 1).TrimEnd(), "…");
    }
}
