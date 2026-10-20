using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMesh.Api.Hubs;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Controllers;

public record ProjectJobDto(
    Guid Id,
    string ProjectId,
    string? ProjectName,
    string Title,
    string Description,
    ProjectJobStatus Status,
    string GitBranchName,
    string? S3ArchiveKey,
    List<JobTaskDto> Tasks,
    string? ResultSummary,
    int MessageCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record CreateProjectJobRequest(
    string Title,
    string? Description = null,
    string? GitBranchName = null,
    List<string>? InitialTasks = null
);

public record UpdateProjectJobRequest(
    string? Title = null,
    string? Description = null,
    ProjectJobStatus? Status = null,
    string? ResultSummary = null
);

public record CreateJobTaskRequest(
    string Title,
    string? AssignedPersonaId = null
);

public record UpdateJobTaskRequest(
    string? Title = null,
    bool? IsCompleted = null,
    string? AssignedPersonaId = null
);

public record PostJobMessageRequest(
    string Content,
    string SenderType = "user",
    string? SenderId = null,
    string? SenderName = null,
    Guid? TurnSessionId = null
);

public record CompleteJobRequest(
    string? ResultSummary = null
);

public record JobMessageDto(
    Guid Id,
    Guid JobId,
    string SenderType,
    string? SenderId,
    string? SenderName,
    string Content,
    List<string> Mentions,
    Guid? TurnSessionId,
    string? StructuredPayloadJson,
    DateTimeOffset CreatedAt
);

[ApiController]
[Authorize]
public class ProjectJobsController(
    AgentMeshDbContext db,
    IHubContext<AgentMeshHub>? hubContext = null) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [HttpGet("api/projects/{projectId}/jobs")]
    [AllowAnonymous]
    public async Task<IActionResult> GetProjectJobs(string projectId)
    {
        var normId = projectId.Trim().ToLowerInvariant();
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == normId);
        if (project == null)
        {
            return NotFound(new { error = $"Project '{projectId}' not found." });
        }

        var jobs = await db.ProjectJobs
            .AsNoTracking()
            .Where(j => j.ProjectId == normId)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync();

        var jobIds = jobs.Select(j => j.Id).ToList();
        var messageCounts = await db.JobMessages
            .AsNoTracking()
            .Where(m => jobIds.Contains(m.JobId))
            .GroupBy(m => m.JobId)
            .Select(g => new { JobId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.JobId, x => x.Count);

        var dtos = jobs.Select(j => ToDto(j, project.Name, messageCounts.GetValueOrDefault(j.Id, 0))).ToList();
        return Ok(dtos);
    }

    [HttpGet("api/project-jobs/{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetJob(Guid id)
    {
        var job = await db.ProjectJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id);
        if (job == null)
        {
            return NotFound(new { error = $"Job '{id}' not found." });
        }

        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == job.ProjectId);
        var msgCount = await db.JobMessages.CountAsync(m => m.JobId == id);

        return Ok(ToDto(job, project?.Name, msgCount));
    }

    [HttpPost("api/projects/{projectId}/jobs")]
    public async Task<IActionResult> CreateJob(string projectId, [FromBody] CreateProjectJobRequest req)
    {
        var normId = projectId.Trim().ToLowerInvariant();
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == normId);
        if (project == null)
        {
            return NotFound(new { error = $"Project '{projectId}' not found." });
        }

        if (string.IsNullOrWhiteSpace(req.Title))
        {
            return BadRequest(new { error = "Job Title is required." });
        }

        var jobId = Guid.NewGuid();
        var shortId = jobId.ToString("N")[..8];
        var branch = !string.IsNullOrWhiteSpace(req.GitBranchName)
            ? req.GitBranchName.Trim()
            : $"mesh/job-{shortId}";

        var tasks = new List<JobTaskDto>();
        if (req.InitialTasks is { Count: > 0 })
        {
            foreach (var tTitle in req.InitialTasks)
            {
                if (!string.IsNullOrWhiteSpace(tTitle))
                {
                    tasks.Add(new JobTaskDto
                    {
                        Id = Guid.NewGuid(),
                        Title = tTitle.Trim(),
                        IsCompleted = false
                    });
                }
            }
        }

        var job = new ProjectJob
        {
            Id = jobId,
            ProjectId = normId,
            Title = req.Title.Trim(),
            Description = req.Description?.Trim() ?? string.Empty,
            Status = ProjectJobStatus.Active,
            GitBranchName = branch,
            S3ArchiveKey = project.WorkspaceType == WorkspaceType.S3Archive
                ? $"projects/{normId}/jobs/{jobId}/workspace.tar.gz"
                : null,
            TasksJson = JsonSerializer.Serialize(tasks, JsonOpts),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.ProjectJobs.Add(job);
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var dto = ToDto(job, project.Name, 0);

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("JobCreated", dto);
        }

        return CreatedAtAction(nameof(GetJob), new { id = job.Id }, dto);
    }

    [HttpPut("api/project-jobs/{id}")]
    public async Task<IActionResult> UpdateJob(Guid id, [FromBody] UpdateProjectJobRequest req)
    {
        var job = await db.ProjectJobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null)
        {
            return NotFound(new { error = $"Job '{id}' not found." });
        }

        if (!string.IsNullOrWhiteSpace(req.Title)) job.Title = req.Title.Trim();
        if (req.Description != null) job.Description = req.Description.Trim();
        if (req.Status.HasValue) job.Status = req.Status.Value;
        if (req.ResultSummary != null) job.ResultSummary = req.ResultSummary.Trim();

        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == job.ProjectId);
        var msgCount = await db.JobMessages.CountAsync(m => m.JobId == id);
        var dto = ToDto(job, project?.Name, msgCount);

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("JobUpdated", dto);
        }

        return Ok(dto);
    }

    [HttpDelete("api/project-jobs/{id}")]
    public async Task<IActionResult> DeleteJob(Guid id)
    {
        var job = await db.ProjectJobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null)
        {
            return NotFound(new { error = $"Job '{id}' not found." });
        }

        var messages = await db.JobMessages.Where(m => m.JobId == id).ToListAsync();
        db.JobMessages.RemoveRange(messages);
        db.ProjectJobs.Remove(job);
        await db.SaveChangesAsync();

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("JobDeleted", new { id, projectId = job.ProjectId });
        }

        return Ok(new { status = "deleted", id, projectId = job.ProjectId });
    }

    [HttpGet("api/project-jobs/{id}/tasks")]
    [AllowAnonymous]
    public async Task<IActionResult> GetJobTasks(Guid id)
    {
        var job = await db.ProjectJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id);
        if (job == null)
        {
            return NotFound(new { error = $"Job '{id}' not found." });
        }

        var tasks = ParseTasks(job.TasksJson);
        return Ok(tasks);
    }

    [HttpPost("api/project-jobs/{id}/tasks")]
    public async Task<IActionResult> AddJobTask(Guid id, [FromBody] CreateJobTaskRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
        {
            return BadRequest(new { error = "Task Title is required." });
        }

        var job = await db.ProjectJobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null)
        {
            return NotFound(new { error = $"Job '{id}' not found." });
        }

        var tasks = ParseTasks(job.TasksJson);
        var newTask = new JobTaskDto
        {
            Id = Guid.NewGuid(),
            Title = req.Title.Trim(),
            IsCompleted = false,
            AssignedPersonaId = req.AssignedPersonaId?.Trim().ToLowerInvariant()
        };
        tasks.Add(newTask);

        job.TasksJson = JsonSerializer.Serialize(tasks, JsonOpts);
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("JobTaskUpdated", new { jobId = id, tasks });
        }

        return Ok(newTask);
    }

    [HttpPut("api/project-jobs/{id}/tasks/{taskId}")]
    public async Task<IActionResult> UpdateJobTask(Guid id, Guid taskId, [FromBody] UpdateJobTaskRequest req)
    {
        var job = await db.ProjectJobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null)
        {
            return NotFound(new { error = $"Job '{id}' not found." });
        }

        var tasks = ParseTasks(job.TasksJson);
        var task = tasks.FirstOrDefault(t => t.Id == taskId);
        if (task == null)
        {
            return NotFound(new { error = $"Task '{taskId}' not found." });
        }

        if (!string.IsNullOrWhiteSpace(req.Title)) task.Title = req.Title.Trim();
        if (req.IsCompleted.HasValue)
        {
            task.IsCompleted = req.IsCompleted.Value;
            task.CompletedAt = req.IsCompleted.Value ? DateTimeOffset.UtcNow : null;
        }
        if (req.AssignedPersonaId != null)
        {
            task.AssignedPersonaId = req.AssignedPersonaId.Trim().ToLowerInvariant();
        }

        job.TasksJson = JsonSerializer.Serialize(tasks, JsonOpts);
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("JobTaskUpdated", new { jobId = id, tasks });
        }

        return Ok(task);
    }

    [HttpDelete("api/project-jobs/{id}/tasks/{taskId}")]
    public async Task<IActionResult> DeleteJobTask(Guid id, Guid taskId)
    {
        var job = await db.ProjectJobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null)
        {
            return NotFound(new { error = $"Job '{id}' not found." });
        }

        var tasks = ParseTasks(job.TasksJson);
        var removed = tasks.RemoveAll(t => t.Id == taskId);
        if (removed == 0)
        {
            return NotFound(new { error = $"Task '{taskId}' not found." });
        }

        job.TasksJson = JsonSerializer.Serialize(tasks, JsonOpts);
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("JobTaskUpdated", new { jobId = id, tasks });
        }

        return Ok(new { status = "deleted", taskId });
    }

    [HttpGet("api/project-jobs/{id}/messages")]
    [AllowAnonymous]
    public async Task<IActionResult> GetJobMessages(Guid id)
    {
        var job = await db.ProjectJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id);
        if (job == null)
        {
            return NotFound(new { error = $"Job '{id}' not found." });
        }

        var messages = await db.JobMessages
            .AsNoTracking()
            .Where(m => m.JobId == id)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();

        var dtos = messages.Select(ToMessageDto).ToList();
        return Ok(dtos);
    }

    [HttpPost("api/project-jobs/{id}/messages")]
    public async Task<IActionResult> PostJobMessage(Guid id, [FromBody] PostJobMessageRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Content))
        {
            return BadRequest(new { error = "Message content is required." });
        }

        var job = await db.ProjectJobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null)
        {
            return NotFound(new { error = $"Job '{id}' not found." });
        }

        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == job.ProjectId);
        if (project == null)
        {
            return NotFound(new { error = $"Project '{job.ProjectId}' not found." });
        }

        // Parse @mentions
        var matches = Regex.Matches(req.Content, @"@([a-zA-Z0-9_\-]+)");
        var mentions = matches.Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().ToList();

        var assignedPersonaIds = await db.ProjectPersonas
            .AsNoTracking()
            .Where(pp => pp.ProjectId == project.Id)
            .Select(pp => pp.PersonaId)
            .ToListAsync();

        // Check if an assigned persona was mentioned
        string? targetPersonaId = mentions.FirstOrDefault(m => assignedPersonaIds.Contains(m));

        // If no explicit mention and message comes from user, fall back to DefaultLeadPersonaId if assigned
        if (targetPersonaId == null && req.SenderType == "user" && !string.IsNullOrWhiteSpace(project.DefaultLeadPersonaId))
        {
            if (assignedPersonaIds.Contains(project.DefaultLeadPersonaId))
            {
                targetPersonaId = project.DefaultLeadPersonaId;
                if (!mentions.Contains(targetPersonaId))
                {
                    mentions.Add(targetPersonaId);
                }
            }
        }

        var jobMsg = new JobMessage
        {
            Id = Guid.NewGuid(),
            JobId = id,
            SenderType = req.SenderType,
            SenderId = req.SenderId,
            SenderName = req.SenderName,
            Content = req.Content.Trim(),
            MentionsJson = JsonSerializer.Serialize(mentions, JsonOpts),
            TurnSessionId = req.TurnSessionId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        // If a target persona was chosen and no TurnSessionId was provided, initiate execution turn!
        if (targetPersonaId != null && req.TurnSessionId == null)
        {
            var persona = await db.Personas.FirstOrDefaultAsync(p => p.Id == targetPersonaId);
            if (persona != null)
            {
                var driverId = persona.DriverId.Trim().ToLowerInvariant();
                var driver = await db.Agents.FirstOrDefaultAsync(d => d.Id == driverId);
                if (driver == null)
                {
                    driver = new Agent
                    {
                        Id = driverId,
                        Name = $"Driver {driverId}",
                        RuntimeType = RuntimeType.CliAgy,
                        TrustTier = TrustTier.SeniorManager
                    };
                    db.Agents.Add(driver);
                }

                // Create turn metadata
                var turnMetadata = new Dictionary<string, object?>
                {
                    ["jobId"] = job.Id.ToString(),
                    ["projectId"] = project.Id,
                    ["projectName"] = project.Name,
                    ["workspaceType"] = project.WorkspaceType.ToString(),
                    ["gitBranch"] = job.GitBranchName,
                    ["gitUrl"] = project.GitRepositoryUrl,
                    ["gitBaseBranch"] = project.GitBaseBranch,
                    ["s3ArchiveKey"] = job.S3ArchiveKey,
                    ["projectInstructions"] = project.ProjectInstructions,
                    ["personaId"] = persona.Id,
                    ["personaName"] = persona.Name,
                    ["personaRole"] = persona.Role,
                    ["personaPrompt"] = persona.PersonaPrompt,
                    ["workingDirectory"] = persona.DefaultWorkingDirectory,
                    ["model"] = persona.DefaultModel,
                    ["effort"] = persona.DefaultEffort,
                    ["mode"] = persona.DefaultMode,
                    ["tier"] = persona.DefaultTier
                };

                var turnSession = new AgentSession
                {
                    Id = Guid.NewGuid(),
                    Title = $"Job Turn: {job.Title} (@{persona.Name})",
                    Status = "thinking",
                    MetadataJson = JsonSerializer.Serialize(turnMetadata, JsonOpts),
                    PersonaId = persona.Id,
                    DriverId = driverId,
                    CreatorAgentId = driverId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                db.AgentSessions.Add(turnSession);
                jobMsg.TurnSessionId = turnSession.Id;

                // Format prompt context for runner
                var promptWithContext = $"[Project Context: Project \"{project.Name}\" ({project.WorkspaceType}), Branch: \"{job.GitBranchName}\"]\n" +
                                        (!string.IsNullOrWhiteSpace(project.ProjectInstructions) ? $"[Project Guidelines: {project.ProjectInstructions}]\n" : "") +
                                        $"[Job Context: {job.Title} - {job.Description}]\n\n" +
                                        req.Content.Trim();

                var promptMsg = new SessionMessage
                {
                    SessionId = turnSession.Id,
                    SenderAgentId = req.SenderType == "persona" ? (req.SenderId ?? "agent") : "operator",
                    TargetAgentId = driverId,
                    EventType = "prompt",
                    Content = promptWithContext,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.SessionMessages.Add(promptMsg);

                if (AgentMeshHub.IsAgentConnected(driverId))
                {
                    driver.Status = "busy";
                    driver.LastHeartbeatAt = DateTimeOffset.UtcNow;
                }

                if (hubContext != null)
                {
                    var promptPayload = new
                    {
                        sessionId = turnSession.Id,
                        messageId = promptMsg.Id,
                        agentId = driverId,
                        driverId = driverId,
                        personaId = persona.Id,
                        personaName = persona.Name,
                        personaRole = persona.Role,
                        personaPrompt = persona.PersonaPrompt,
                        prompt = promptMsg.Content,
                        sender = promptMsg.SenderAgentId,
                        workingDirectory = persona.DefaultWorkingDirectory,
                        model = persona.DefaultModel,
                        effort = persona.DefaultEffort,
                        mode = persona.DefaultMode,
                        tier = persona.DefaultTier,
                        jobId = job.Id,
                        projectId = project.Id,
                        gitBranch = job.GitBranchName,
                        createdAt = promptMsg.CreatedAt
                    };

                    await hubContext.Clients.Group($"agent-{driverId}").SendAsync("PromptReceived", promptPayload);
                    await hubContext.Clients.All.SendAsync("PromptReceived", promptPayload);
                }
            }
        }

        db.JobMessages.Add(jobMsg);
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var msgDto = ToMessageDto(jobMsg);

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("JobMessageAdded", msgDto);
        }

        return Ok(msgDto);
    }

    [HttpPost("api/project-jobs/{id}/complete")]
    public async Task<IActionResult> CompleteJob(Guid id, [FromBody] CompleteJobRequest? req = null)
    {
        var job = await db.ProjectJobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null)
        {
            return NotFound(new { error = $"Job '{id}' not found." });
        }

        job.Status = ProjectJobStatus.Completed;
        if (!string.IsNullOrWhiteSpace(req?.ResultSummary))
        {
            job.ResultSummary = req.ResultSummary.Trim();
        }
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == job.ProjectId);
        var msgCount = await db.JobMessages.CountAsync(m => m.JobId == id);
        var dto = ToDto(job, project?.Name, msgCount);

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("JobUpdated", dto);
        }

        return Ok(dto);
    }

    private static List<JobTaskDto> ParseTasks(string tasksJson)
    {
        if (string.IsNullOrWhiteSpace(tasksJson)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<JobTaskDto>>(tasksJson, JsonOpts) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static ProjectJobDto ToDto(ProjectJob j, string? projectName, int msgCount)
    {
        return new ProjectJobDto(
            j.Id,
            j.ProjectId,
            projectName,
            j.Title,
            j.Description,
            j.Status,
            j.GitBranchName,
            j.S3ArchiveKey,
            ParseTasks(j.TasksJson),
            j.ResultSummary,
            msgCount,
            j.CreatedAt,
            j.UpdatedAt
        );
    }

    private static JobMessageDto ToMessageDto(JobMessage m)
    {
        List<string> mentions;
        try
        {
            mentions = JsonSerializer.Deserialize<List<string>>(m.MentionsJson, JsonOpts) ?? new();
        }
        catch
        {
            mentions = new();
        }

        return new JobMessageDto(
            m.Id,
            m.JobId,
            m.SenderType,
            m.SenderId,
            m.SenderName,
            m.Content,
            mentions,
            m.TurnSessionId,
            m.StructuredPayloadJson,
            m.CreatedAt
        );
    }
}
