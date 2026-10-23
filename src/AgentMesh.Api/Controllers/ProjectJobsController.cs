using System.Text.Json;
using System.Text.RegularExpressions;
using AgentMesh.Api.Hubs;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
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
    DateTimeOffset UpdatedAt,
    ProjectJobPriority Priority = ProjectJobPriority.Normal,
    List<QueuedJobTurnDto>? QueuedTurns = null
);

public record QueuedJobTurnDto(
    Guid Id,
    Guid JobId,
    string ProjectId,
    string TargetPersonaId,
    string SenderType,
    string? SenderId,
    string? SenderName,
    string Content,
    List<string> Mentions,
    List<JobAttachmentDto>? Attachments,
    ProjectJobPriority Priority,
    DateTimeOffset CreatedAt
);

public record CreateProjectJobRequest(
    string Title,
    string? Description = null,
    string? GitBranchName = null,
    List<string>? InitialTasks = null,
    ProjectJobPriority? Priority = null
);

public record UpdateProjectJobRequest(
    string? Title = null,
    string? Description = null,
    ProjectJobStatus? Status = null,
    string? ResultSummary = null,
    ProjectJobPriority? Priority = null
);

public record CreateJobTaskRequest(
    string Title,
    string? AssignedPersonaId = null,
    bool? RequiresDiscussion = false,
    List<JobAttachmentDto>? Attachments = null
);

public record UpdateJobTaskRequest(
    string? Title = null,
    bool? IsCompleted = null,
    string? AssignedPersonaId = null,
    bool? RequiresDiscussion = null,
    List<JobAttachmentDto>? Attachments = null
);

public record PostJobMessageRequest(
    string Content,
    string SenderType = "user",
    string? SenderId = null,
    string? SenderName = null,
    Guid? TurnSessionId = null,
    List<JobAttachmentDto>? Attachments = null
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
    DateTimeOffset CreatedAt,
    List<JobAttachmentDto>? Attachments = null
);

[ApiController]
[Authorize]
public class ProjectJobsController(
    AgentMeshDbContext db,
    IHubContext<AgentMeshHub>? hubContext = null,
    IStorageService? storageService = null) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [HttpGet("api/projects/{projectId}/jobs")]
    [AllowAnonymous]
    public async Task<IActionResult> GetProjectJobs(string projectId, [FromQuery] bool? includeArchived = true)
    {
        var normId = projectId.Trim().ToLowerInvariant();
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == normId);
        if (project == null)
        {
            return NotFound(new { error = $"Project '{projectId}' not found." });
        }

        var query = db.ProjectJobs
            .AsNoTracking()
            .Where(j => j.ProjectId == normId);

        if (includeArchived == false)
        {
            query = query.Where(j => j.Status != ProjectJobStatus.Archived);
        }

        var jobs = await query
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
            Priority = req.Priority ?? ProjectJobPriority.Normal,
            GitBranchName = branch,
            S3ArchiveKey = project.WorkspaceType == WorkspaceType.S3Archive
                ? $"projects/{normId}/jobs/{jobId}/workspace.tar.gz"
                : null,
            TasksJson = JsonSerializer.Serialize(tasks, JsonOpts),
            QueuedTurnsJson = "[]",
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
        if (req.Priority.HasValue) job.Priority = req.Priority.Value;
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
            RequiresDiscussion = req.RequiresDiscussion ?? false,
            AssignedPersonaId = req.AssignedPersonaId?.Trim().ToLowerInvariant(),
            Attachments = req.Attachments ?? new()
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
        if (req.RequiresDiscussion.HasValue)
        {
            task.RequiresDiscussion = req.RequiresDiscussion.Value;
        }
        if (req.Attachments != null)
        {
            task.Attachments = req.Attachments;
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

    [HttpPost("api/project-jobs/{id}/tasks/{taskId}/attachments")]
    [RequestSizeLimit(262_144_000)]
    public async Task<IActionResult> UploadTaskAttachments(Guid id, Guid taskId, [FromForm] IFormFileCollection? files = null)
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

        var formFiles = files ?? (Request?.HasFormContentType == true ? Request.Form.Files : null);
        if (formFiles == null || formFiles.Count == 0)
        {
            return BadRequest(new { error = "No files uploaded." });
        }

        if (formFiles.Count > 10)
        {
            return BadRequest(new { error = "Maximum of 10 files can be uploaded per request." });
        }

        const long maxSizeBytes = 25 * 1024 * 1024;
        foreach (var file in formFiles)
        {
            if (file.Length <= 0) return BadRequest(new { error = $"File '{file.FileName}' is empty." });
            if (file.Length > maxSizeBytes) return BadRequest(new { error = $"File '{file.FileName}' exceeds the 25 MB maximum file size limit." });
            if (!SessionsController.IsAllowedAttachment(file.FileName, file.ContentType)) return BadRequest(new { error = $"File '{file.FileName}' has an unsupported file type." });
        }

        var uploaded = new List<JobAttachmentDto>();
        var localAttachDir = GetAttachmentDirectory(id);

        foreach (var file in formFiles)
        {
            var attachmentId = Guid.NewGuid().ToString("N");
            var safeFileName = SessionsController.SanitizeFileName(file.FileName);
            var contentType = !string.IsNullOrWhiteSpace(file.ContentType) && !string.Equals(file.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
                ? file.ContentType
                : SessionsController.GuessContentTypeFromExtension(safeFileName);
            var s3Key = $"jobs/{id}/attachments/{attachmentId}/{safeFileName}";
            var downloadUrl = $"/api/project-jobs/{id}/attachments/{attachmentId}/content";

            var localFilePath = Path.Combine(localAttachDir, $"{attachmentId}_{safeFileName}");
            await using (var localStream = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                await file.CopyToAsync(localStream, HttpContext?.RequestAborted ?? CancellationToken.None);
            }

            if (storageService != null)
            {
                try
                {
                    await using var s3Stream = file.OpenReadStream();
                    await storageService.UploadStreamAsync(s3Key, s3Stream, contentType, HttpContext?.RequestAborted ?? CancellationToken.None);
                }
                catch
                {
                    // Graceful fallback to local disk if S3 is unreachable
                }
            }

            var dto = new JobAttachmentDto(
                Id: attachmentId,
                JobId: id,
                FileName: safeFileName,
                ContentType: contentType,
                SizeBytes: file.Length,
                S3Key: s3Key,
                DownloadUrl: downloadUrl,
                CreatedAt: DateTimeOffset.UtcNow
            );

            uploaded.Add(dto);
        }

        task.Attachments ??= new List<JobAttachmentDto>();
        task.Attachments.AddRange(uploaded);

        job.TasksJson = JsonSerializer.Serialize(tasks, JsonOpts);
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("JobTaskUpdated", new { jobId = id, tasks });
        }

        return Ok(uploaded);
    }

    [HttpDelete("api/project-jobs/{id}/tasks/{taskId}/attachments/{attachmentId}")]
    public async Task<IActionResult> DeleteTaskAttachment(Guid id, Guid taskId, string attachmentId)
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

        if (task.Attachments == null || task.Attachments.RemoveAll(a => a.Id == attachmentId) == 0)
        {
            return NotFound(new { error = $"Attachment '{attachmentId}' not found in task '{taskId}'." });
        }

        job.TasksJson = JsonSerializer.Serialize(tasks, JsonOpts);
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("JobTaskUpdated", new { jobId = id, tasks });
        }

        return Ok(new { status = "deleted", attachmentId });
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

    [HttpPost("api/project-jobs/{id}/attachments")]
    [RequestSizeLimit(262_144_000)]
    public async Task<IActionResult> UploadJobAttachments(Guid id, [FromForm] IFormFileCollection? files = null)
    {
        var job = await db.ProjectJobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null)
        {
            return NotFound(new { error = $"Job '{id}' not found." });
        }

        var formFiles = files ?? (Request?.HasFormContentType == true ? Request.Form.Files : null);
        if (formFiles == null || formFiles.Count == 0)
        {
            return BadRequest(new { error = "No files uploaded." });
        }

        if (formFiles.Count > 10)
        {
            return BadRequest(new { error = "Maximum of 10 files can be uploaded per message." });
        }

        const long maxSizeBytes = 25 * 1024 * 1024;
        foreach (var file in formFiles)
        {
            if (file.Length <= 0)
            {
                return BadRequest(new { error = $"File '{file.FileName}' is empty." });
            }

            if (file.Length > maxSizeBytes)
            {
                return BadRequest(new { error = $"File '{file.FileName}' exceeds the 25 MB maximum file size limit." });
            }

            if (!SessionsController.IsAllowedAttachment(file.FileName, file.ContentType))
            {
                return BadRequest(new { error = $"File '{file.FileName}' has an unsupported file type." });
            }
        }

        var uploaded = new List<JobAttachmentDto>();
        var localAttachDir = GetAttachmentDirectory(id);

        foreach (var file in formFiles)
        {
            var attachmentId = Guid.NewGuid().ToString("N");
            var safeFileName = SessionsController.SanitizeFileName(file.FileName);
            var contentType = !string.IsNullOrWhiteSpace(file.ContentType) && !string.Equals(file.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
                ? file.ContentType
                : SessionsController.GuessContentTypeFromExtension(safeFileName);
            var s3Key = $"jobs/{id}/attachments/{attachmentId}/{safeFileName}";
            var downloadUrl = $"/api/project-jobs/{id}/attachments/{attachmentId}/content";

            var localFilePath = Path.Combine(localAttachDir, $"{attachmentId}_{safeFileName}");
            await using (var localStream = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                await file.CopyToAsync(localStream, HttpContext?.RequestAborted ?? CancellationToken.None);
            }

            if (storageService != null)
            {
                try
                {
                    await using var s3Stream = file.OpenReadStream();
                    await storageService.UploadStreamAsync(s3Key, s3Stream, contentType, HttpContext?.RequestAborted ?? CancellationToken.None);
                }
                catch
                {
                    // Graceful fallback to local disk if S3 is unreachable
                }
            }

            var dto = new JobAttachmentDto(
                Id: attachmentId,
                JobId: id,
                FileName: safeFileName,
                ContentType: contentType,
                SizeBytes: file.Length,
                S3Key: s3Key,
                DownloadUrl: downloadUrl,
                CreatedAt: DateTimeOffset.UtcNow
            );

            uploaded.Add(dto);
        }

        return Ok(uploaded);
    }

    [HttpGet("api/project-jobs/{id}/attachments/{attachmentId}/content")]
    [AllowAnonymous]
    public async Task<IActionResult> GetAttachmentContent(Guid id, string attachmentId, [FromQuery] bool download = false)
    {
        var job = await db.ProjectJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id);
        if (job == null)
        {
            return NotFound(new { error = $"Job '{id}' not found." });
        }

        var localAttachDir = GetAttachmentDirectory(id);
        string? localFilePath = null;
        string? safeName = null;

        if (Directory.Exists(localAttachDir))
        {
            var matchingFile = Directory.GetFiles(localAttachDir, $"{attachmentId}_*").FirstOrDefault()
                ?? Directory.GetFiles(localAttachDir, $"*{attachmentId}*").FirstOrDefault();
            if (matchingFile != null)
            {
                localFilePath = matchingFile;
                var fn = Path.GetFileName(matchingFile);
                var underscoreIdx = fn.IndexOf('_');
                safeName = underscoreIdx >= 0 ? fn[(underscoreIdx + 1)..] : fn;
            }
        }

        if (localFilePath == null)
        {
            // Also check session artifacts folder as fallback
            var sessionAttachDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentmesh", "artifacts", id.ToString(), "attachments");
            if (Directory.Exists(sessionAttachDir))
            {
                var candidate = Directory.GetFiles(sessionAttachDir, $"{attachmentId}_*").FirstOrDefault()
                    ?? Directory.GetFiles(sessionAttachDir, $"*{attachmentId}*").FirstOrDefault();
                if (candidate != null)
                {
                    localFilePath = candidate;
                    var fn = Path.GetFileName(candidate);
                    var underscoreIdx = fn.IndexOf('_');
                    safeName = underscoreIdx >= 0 ? fn[(underscoreIdx + 1)..] : fn;
                }
            }
        }

        Stream? contentStream = null;
        if (localFilePath != null && System.IO.File.Exists(localFilePath))
        {
            contentStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        else if (storageService != null)
        {
            try
            {
                var s3Key = $"jobs/{id}/attachments/{attachmentId}/{safeName ?? "attachment"}";
                contentStream = await storageService.DownloadStreamAsync(s3Key, HttpContext?.RequestAborted ?? CancellationToken.None);
            }
            catch
            {
                contentStream = null;
            }
        }

        if (contentStream == null)
        {
            return NotFound(new { error = $"Attachment '{attachmentId}' not found for job {id}." });
        }

        var contentType = SessionsController.GuessContentTypeFromExtension(safeName ?? "attachment.bin");
        var isInlineRenderable = !download && (
            contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
            contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) ||
            contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase));

        var disposition = isInlineRenderable ? "inline" : "attachment";
        if (Response != null)
        {
            Response.Headers.ContentDisposition = $"{disposition}; filename=\"{SessionsController.SanitizeFileName(safeName)}\"";
        }

        return File(contentStream, contentType);
    }

    [HttpPost("api/project-jobs/{id}/messages")]
    public async Task<IActionResult> PostJobMessage(Guid id, [FromBody] PostJobMessageRequest req)
    {
        var effectiveContent = !string.IsNullOrWhiteSpace(req.Content)
            ? req.Content.Trim()
            : (req.Attachments is { Count: > 0 } ? "Please review the attached file(s)." : string.Empty);

        if (string.IsNullOrWhiteSpace(effectiveContent))
        {
            return BadRequest(new { error = "Message content or at least one attachment is required." });
        }

        var job = await db.ProjectJobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null)
        {
            return NotFound(new { error = $"Job '{id}' not found." });
        }

        if (job.Status == ProjectJobStatus.Archived)
        {
            return BadRequest(new { error = "Cannot post messages to an archived job. Please reopen the job first." });
        }

        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == job.ProjectId);
        if (project == null)
        {
            return NotFound(new { error = $"Project '{job.ProjectId}' not found." });
        }

        // Parse @mentions
        var matches = Regex.Matches(effectiveContent, @"@([a-zA-Z0-9_\-]+)");
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

        string? structuredPayloadJson = null;
        if (req.Attachments is { Count: > 0 })
        {
            structuredPayloadJson = JsonSerializer.Serialize(new { attachments = req.Attachments }, JsonOpts);
        }

        var jobMsg = new JobMessage
        {
            Id = Guid.NewGuid(),
            JobId = id,
            SenderType = req.SenderType,
            SenderId = req.SenderId,
            SenderName = req.SenderName,
            Content = effectiveContent,
            MentionsJson = JsonSerializer.Serialize(mentions, JsonOpts),
            TurnSessionId = req.TurnSessionId,
            StructuredPayloadJson = structuredPayloadJson,
            CreatedAt = DateTimeOffset.UtcNow
        };

        JobMessage? ackMsg = null;
        AgentSession? turnSession = null;
        Persona? turnPersona = null;
        Agent? turnDriver = null;

        // If a target persona was chosen and no TurnSessionId was provided, initiate execution turn or queue!
        if (targetPersonaId != null && req.TurnSessionId == null)
        {
            var persona = await db.Personas.FirstOrDefaultAsync(p => p.Id == targetPersonaId);
            if (persona != null)
            {
                // Check MaxConcurrentJobs limit
                bool shouldQueue = false;
                string? busyJobTitle = null;

                if (persona.MaxConcurrentJobs.HasValue && persona.MaxConcurrentJobs.Value > 0)
                {
                    var activeTurnSessions = await db.AgentSessions
                        .AsNoTracking()
                        .Where(s => s.PersonaId == persona.Id && (s.Status == "thinking" || s.Status == "busy"))
                        .ToListAsync();

                    var busyJobIds = activeTurnSessions
                        .Select(s => ExtractJobId(s.MetadataJson))
                        .Where(jid => jid.HasValue && jid.Value != id)
                        .Select(jid => jid!.Value)
                        .Distinct()
                        .ToList();

                    if (busyJobIds.Count >= persona.MaxConcurrentJobs.Value)
                    {
                        shouldQueue = true;
                        var firstBusyJob = await db.ProjectJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == busyJobIds[0]);
                        busyJobTitle = firstBusyJob?.Title ?? "another job";
                    }
                }

                if (shouldQueue)
                {
                    var queuedTurns = ParseQueuedTurns(job.QueuedTurnsJson);
                    var queuedTurn = new QueuedJobTurnDto(
                        Id: Guid.NewGuid(),
                        JobId: id,
                        ProjectId: project.Id,
                        TargetPersonaId: persona.Id,
                        SenderType: req.SenderType,
                        SenderId: req.SenderId,
                        SenderName: req.SenderName,
                        Content: effectiveContent,
                        Mentions: mentions,
                        Attachments: req.Attachments,
                        Priority: job.Priority,
                        CreatedAt: DateTimeOffset.UtcNow
                    );
                    queuedTurns.Add(queuedTurn);
                    job.QueuedTurnsJson = JsonSerializer.Serialize(queuedTurns, JsonOpts);
                    job.UpdatedAt = DateTimeOffset.UtcNow;

                    db.JobMessages.Add(jobMsg);

                    var busyContent = $"I'm currently busy working on another job (\"{busyJobTitle}\"), but I've queued this up and will start as soon as I'm done!";
                    var busyMsg = new JobMessage
                    {
                        Id = Guid.NewGuid(),
                        JobId = id,
                        SenderType = "persona",
                        SenderId = persona.Id,
                        SenderName = persona.Name,
                        Content = busyContent,
                        MentionsJson = JsonSerializer.Serialize(new List<string> { persona.Id }, JsonOpts),
                        TurnSessionId = null,
                        CreatedAt = DateTimeOffset.UtcNow
                    };
                    db.JobMessages.Add(busyMsg);
                    await db.SaveChangesAsync();

                    if (hubContext != null)
                    {
                        await hubContext.Clients.All.SendAsync("JobMessageCreated", ToMessageDto(jobMsg));
                        await hubContext.Clients.All.SendAsync("JobMessageCreated", ToMessageDto(busyMsg));
                        var msgCount = await db.JobMessages.CountAsync(m => m.JobId == id);
                        var jobDto = ToDto(job, project.Name, msgCount);
                        await hubContext.Clients.All.SendAsync("JobUpdated", jobDto);
                    }

                    return Ok(ToMessageDto(jobMsg));
                }

                turnPersona = persona;
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
                turnDriver = driver;

                var turnSessionId = Guid.NewGuid();

                var tasks = ParseTasks(job.TasksJson);
                var activeTask = tasks.FirstOrDefault(t => !t.IsCompleted);

                // Combine message attachments with active task attachments so the agent gets both
                var combinedAttachments = new List<JobAttachmentDto>();
                if (req.Attachments is { Count: > 0 })
                {
                    combinedAttachments.AddRange(req.Attachments);
                }
                if (activeTask?.Attachments is { Count: > 0 })
                {
                    foreach (var att in activeTask.Attachments)
                    {
                        if (!combinedAttachments.Any(a => a.Id == att.Id))
                        {
                            combinedAttachments.Add(att);
                        }
                    }
                }

                // If attachments exist, stage them into the turn session's attachment directory
                if (combinedAttachments.Count > 0)
                {
                    var sessionAttachDir = SessionsController.GetAttachmentDirectory(turnSessionId);
                    var jobAttachDir = GetAttachmentDirectory(id);

                    foreach (var att in combinedAttachments)
                    {
                        var safeName = SessionsController.SanitizeFileName(att.FileName);
                        var src = Path.Combine(jobAttachDir, $"{att.Id}_{safeName}");
                        if (!System.IO.File.Exists(src))
                        {
                            src = Directory.GetFiles(jobAttachDir, $"{att.Id}_*").FirstOrDefault() ??
                                  Directory.GetFiles(jobAttachDir, $"*{att.Id}*").FirstOrDefault();
                        }
                        if (src != null && System.IO.File.Exists(src))
                        {
                            var dest = Path.Combine(sessionAttachDir, $"{att.Id}_{safeName}");
                            try
                            {
                                System.IO.File.Copy(src, dest, overwrite: true);
                            }
                            catch { }
                        }
                    }
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
                    ["tier"] = persona.DefaultTier,
                    ["contextSize"] = persona.DefaultContextSize,
                    ["attachments"] = combinedAttachments
                };

                turnSession = new AgentSession
                {
                    Id = turnSessionId,
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

                var taskContext = FormatTaskContext(tasks);

                // Format prompt context for runner
                var promptWithContext = $"[Project Context: Project \"{project.Name}\" ({project.WorkspaceType}), Branch: \"{job.GitBranchName}\"]\n" +
                                        (!string.IsNullOrWhiteSpace(project.ProjectInstructions) ? $"[Project Guidelines: {project.ProjectInstructions}]\n" : "") +
                                        $"[Job Context: {job.Title} - {job.Description}]\n\n" +
                                        taskContext +
                                        effectiveContent;

                var promptPayloadJson = combinedAttachments.Count > 0
                    ? JsonSerializer.Serialize(new { attachments = combinedAttachments }, JsonOpts)
                    : structuredPayloadJson;

                var promptMsg = new SessionMessage
                {
                    SessionId = turnSession.Id,
                    SenderAgentId = req.SenderType == "persona" ? (req.SenderId ?? "agent") : "operator",
                    TargetAgentId = driverId,
                    EventType = "prompt",
                    Content = promptWithContext,
                    StructuredPayloadJson = promptPayloadJson,
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
                        contextSize = persona.DefaultContextSize,
                        jobId = job.Id,
                        projectId = project.Id,
                        gitBranch = job.GitBranchName,
                        createdAt = promptMsg.CreatedAt,
                        attachments = combinedAttachments
                    };

                    await hubContext.Clients.Group($"agent-{driverId}").SendAsync("PromptReceived", promptPayload);
                    await hubContext.Clients.All.SendAsync("PromptReceived", promptPayload);
                }

                if (req.SenderType == "user")
                {
                    var ackContent = activeTask != null
                        ? (activeTask.RequiresDiscussion
                            ? $"I'm on it — starting discussion on \"{activeTask.Title}\" before coding."
                            : $"I'm on it — starting work on \"{activeTask.Title}\" now.")
                        : "I'm on it — starting work on this now.";

                    ackMsg = new JobMessage
                    {
                        Id = Guid.NewGuid(),
                        JobId = id,
                        SenderType = "persona",
                        SenderId = persona.Id,
                        SenderName = persona.Name,
                        Content = ackContent,
                        MentionsJson = JsonSerializer.Serialize(new List<string>(), JsonOpts),
                        TurnSessionId = turnSession.Id,
                        CreatedAt = DateTimeOffset.UtcNow.AddMilliseconds(2)
                    };
                    db.JobMessages.Add(ackMsg);
                }
            }
        }

        db.JobMessages.Add(jobMsg);
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var msgDto = ToMessageDto(jobMsg);

        if (hubContext != null)
        {
            if (turnSession != null && turnPersona != null && turnDriver != null)
            {
                await hubContext.Clients.All.SendAsync("SessionCreated", new SessionSummaryDto(
                    turnSession.Id,
                    turnSession.Title,
                    turnSession.Status,
                    turnSession.MetadataJson,
                    turnSession.DriverId,
                    1,
                    turnSession.CreatedAt,
                    turnSession.UpdatedAt,
                    DriverId: turnSession.DriverId,
                    DriverName: turnDriver.Name,
                    PersonaId: turnPersona.Id,
                    PersonaName: turnPersona.Name
                ));
            }

            await hubContext.Clients.All.SendAsync("JobMessageAdded", msgDto);
            if (ackMsg != null)
            {
                var ackDto = ToMessageDto(ackMsg);
                await hubContext.Clients.All.SendAsync("JobMessageAdded", ackDto);
            }
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

    [HttpPost("api/project-jobs/{id}/archive")]
    public async Task<IActionResult> ArchiveJob(Guid id)
    {
        var job = await db.ProjectJobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null)
        {
            return NotFound(new { error = $"Job '{id}' not found." });
        }

        job.Status = ProjectJobStatus.Archived;
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

    [HttpPost("api/project-jobs/{id}/reopen")]
    public async Task<IActionResult> ReopenJob(Guid id)
    {
        var job = await db.ProjectJobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null)
        {
            return NotFound(new { error = $"Job '{id}' not found." });
        }

        job.Status = ProjectJobStatus.Active;
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

    [HttpDelete("api/project-jobs/{id}/queued-turns/{turnId}")]
    public async Task<IActionResult> CancelQueuedTurn(Guid id, Guid turnId)
    {
        var job = await db.ProjectJobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null)
        {
            return NotFound(new { error = $"Job '{id}' not found." });
        }

        var queuedTurns = ParseQueuedTurns(job.QueuedTurnsJson);
        var turnToRemove = queuedTurns.FirstOrDefault(t => t.Id == turnId);
        if (turnToRemove == null)
        {
            return NotFound(new { error = $"Queued turn '{turnId}' not found on job '{id}'." });
        }

        queuedTurns.RemoveAll(t => t.Id == turnId);
        job.QueuedTurnsJson = JsonSerializer.Serialize(queuedTurns, JsonOpts);
        job.UpdatedAt = DateTimeOffset.UtcNow;

        var persona = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == turnToRemove.TargetPersonaId);
        var personaName = persona?.Name ?? turnToRemove.TargetPersonaId;
        var cancelMsg = new JobMessage
        {
            Id = Guid.NewGuid(),
            JobId = id,
            SenderType = "system",
            SenderId = "operator",
            SenderName = "System",
            Content = $"Queued turn for @{personaName} was cancelled.",
            MentionsJson = JsonSerializer.Serialize(new List<string> { turnToRemove.TargetPersonaId }, JsonOpts),
            TurnSessionId = null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.JobMessages.Add(cancelMsg);

        await db.SaveChangesAsync();

        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == job.ProjectId);
        var msgCount = await db.JobMessages.CountAsync(m => m.JobId == id);
        var dto = ToDto(job, project?.Name, msgCount);

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("JobMessageCreated", ToMessageDto(cancelMsg));
            await hubContext.Clients.All.SendAsync("JobUpdated", dto);
        }

        return Ok(new { message = "Queued turn cancelled successfully.", turnId, job = dto });
    }

    [HttpDelete("api/project-jobs/{id}/queued-turns")]
    public async Task<IActionResult> CancelQueuedTurns(Guid id, [FromQuery] string? personaId = null)
    {
        var job = await db.ProjectJobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job == null)
        {
            return NotFound(new { error = $"Job '{id}' not found." });
        }

        var queuedTurns = ParseQueuedTurns(job.QueuedTurnsJson);
        if (queuedTurns.Count == 0)
        {
            return Ok(new { message = "No queued turns to cancel.", count = 0 });
        }

        List<QueuedJobTurnDto> turnsToRemove;
        if (!string.IsNullOrWhiteSpace(personaId))
        {
            var normPersonaId = personaId.Trim().ToLowerInvariant();
            turnsToRemove = queuedTurns.Where(t => string.Equals(t.TargetPersonaId, normPersonaId, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        else
        {
            turnsToRemove = queuedTurns.ToList();
        }

        if (turnsToRemove.Count == 0)
        {
            return Ok(new { message = "No matching queued turns to cancel.", count = 0 });
        }

        queuedTurns.RemoveAll(t => turnsToRemove.Any(r => r.Id == t.Id));
        job.QueuedTurnsJson = JsonSerializer.Serialize(queuedTurns, JsonOpts);
        job.UpdatedAt = DateTimeOffset.UtcNow;

        var targetPersonaIds = turnsToRemove.Select(t => t.TargetPersonaId).Distinct().ToList();
        var nameById = await db.Personas.AsNoTracking()
            .Where(p => targetPersonaIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name);
        var targetNames = string.Join(", ", targetPersonaIds.Select(pid => "@" + (nameById.TryGetValue(pid, out var n) ? n : pid)));
        var cancelMsg = new JobMessage
        {
            Id = Guid.NewGuid(),
            JobId = id,
            SenderType = "system",
            SenderId = "operator",
            SenderName = "System",
            Content = $"Cancelled {turnsToRemove.Count} queued turn(s) for {targetNames}.",
            MentionsJson = JsonSerializer.Serialize(targetPersonaIds, JsonOpts),
            TurnSessionId = null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.JobMessages.Add(cancelMsg);

        await db.SaveChangesAsync();

        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == job.ProjectId);
        var msgCount = await db.JobMessages.CountAsync(m => m.JobId == id);
        var dto = ToDto(job, project?.Name, msgCount);

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("JobMessageCreated", ToMessageDto(cancelMsg));
            await hubContext.Clients.All.SendAsync("JobUpdated", dto);
        }

        return Ok(new { message = $"Cancelled {turnsToRemove.Count} queued turn(s).", count = turnsToRemove.Count, job = dto });
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

    private static string FormatTaskContext(List<JobTaskDto> tasks)
    {
        if (tasks.Count == 0) return string.Empty;

        var activeTask = tasks.FirstOrDefault(t => !t.IsCompleted);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[Job Tasks & Sequential Checklist]:");
        for (int i = 0; i < tasks.Count; i++)
        {
            var t = tasks[i];
            string statusTag;
            if (t.IsCompleted)
            {
                statusTag = "[x]";
            }
            else if (t.Id == activeTask?.Id)
            {
                statusTag = t.RequiresDiscussion
                    ? "[ ] (CURRENT ACTIVE FOCUS - DISCUSSION REQUIRED)"
                    : "[ ] (CURRENT ACTIVE FOCUS)";
            }
            else
            {
                statusTag = t.RequiresDiscussion
                    ? "[ ] (Queued - Discuss First)"
                    : "[ ] (Queued)";
            }
            var attachInfo = (t.Attachments != null && t.Attachments.Count > 0)
                ? $" (Attachments: {string.Join(", ", t.Attachments.Select(a => a.FileName))})"
                : "";
            sb.AppendLine($"{i + 1}. {statusTag} {t.Title}{attachInfo}");
        }
        if (activeTask != null)
        {
            sb.AppendLine($"[Current Target Task]: \"{activeTask.Title}\"");
            if (activeTask.Attachments is { Count: > 0 })
            {
                sb.AppendLine($"[Target Task Attachments]: {string.Join(", ", activeTask.Attachments.Select(a => $"{a.FileName} ({a.DownloadUrl})"))}");
            }
            sb.AppendLine("[Sequential Execution Rule]: You must work on and complete tasks sequentially one by one in the order listed above. Focus on completing the current active task first before moving to subsequent tasks.");
            if (activeTask.RequiresDiscussion)
            {
                sb.AppendLine("[Discussion Required Rule]: This task is flagged for discussion before starting coding. Do NOT write or edit code yet. Discuss requirements, clarify questions, evaluate design/architecture trade-offs, or conduct an alignment interview with the user. When you and the user have reached alignment and you are ready to begin implementation, conclude your response by including the tag [READY_TO_PROCEED] so the user can review and approve proceeding to code.");
            }
        }
        sb.AppendLine();
        return sb.ToString();
    }

    public static List<QueuedJobTurnDto> ParseQueuedTurns(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<QueuedJobTurnDto>>(json, JsonOpts) ?? new();
        }
        catch
        {
            return new();
        }
    }

    public static Guid? ExtractJobId(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty("jobId", out var elem))
            {
                var val = elem.GetString();
                if (Guid.TryParse(val, out var jid)) return jid;
            }
        }
        catch { }
        return null;
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
            j.UpdatedAt,
            j.Priority,
            ParseQueuedTurns(j.QueuedTurnsJson)
        );
    }

    public static string GetAttachmentDirectory(Guid jobId)
    {
        var basePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agentmesh",
            "artifacts",
            "jobs",
            jobId.ToString(),
            "attachments");
        Directory.CreateDirectory(basePath);
        return basePath;
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

        List<JobAttachmentDto>? attachments = null;
        if (!string.IsNullOrWhiteSpace(m.StructuredPayloadJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(m.StructuredPayloadJson);
                if ((doc.RootElement.TryGetProperty("attachments", out var aElem) ||
                     doc.RootElement.TryGetProperty("Attachments", out aElem)) &&
                    aElem.ValueKind == JsonValueKind.Array)
                {
                    attachments = JsonSerializer.Deserialize<List<JobAttachmentDto>>(aElem.GetRawText(), JsonOpts);
                }
            }
            catch { }
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
            m.CreatedAt,
            attachments
        );
    }

    public static async Task CheckAndDispatchQueuedJobTurnsAsync(
        AgentMeshDbContext db,
        IHubContext<AgentMeshHub>? hubContext,
        string personaId)
    {
        if (string.IsNullOrWhiteSpace(personaId)) return;
        var normPersonaId = personaId.Trim().ToLowerInvariant();
        var persona = await db.Personas.FirstOrDefaultAsync(p => p.Id == normPersonaId);
        if (persona == null) return;

        var candidateJobs = await db.ProjectJobs
            .Where(j => j.Status == ProjectJobStatus.Active && j.QueuedTurnsJson != "[]")
            .ToListAsync();

        if (candidateJobs.Count == 0) return;

        var allQueuedTurns = new List<(ProjectJob Job, QueuedJobTurnDto Turn)>();
        foreach (var job in candidateJobs)
        {
            var turns = ParseQueuedTurns(job.QueuedTurnsJson);
            foreach (var t in turns)
            {
                if (string.Equals(t.TargetPersonaId, normPersonaId, StringComparison.OrdinalIgnoreCase))
                {
                    allQueuedTurns.Add((job, t));
                }
            }
        }

        if (allQueuedTurns.Count == 0) return;

        var orderedTurns = allQueuedTurns
            .OrderByDescending(x => (int)x.Job.Priority)
            .ThenBy(x => x.Turn.CreatedAt)
            .ToList();

        int availableSlots = int.MaxValue;
        if (persona.MaxConcurrentJobs.HasValue && persona.MaxConcurrentJobs.Value > 0)
        {
            var activeTurnSessions = await db.AgentSessions
                .AsNoTracking()
                .Where(s => s.PersonaId == normPersonaId && (s.Status == "thinking" || s.Status == "busy"))
                .ToListAsync();

            var busyJobIds = activeTurnSessions
                .Select(s => ExtractJobId(s.MetadataJson))
                .Where(jid => jid.HasValue)
                .Select(jid => jid!.Value)
                .Distinct()
                .ToHashSet();

            availableSlots = Math.Max(0, persona.MaxConcurrentJobs.Value - busyJobIds.Count);
        }

        if (availableSlots <= 0) return;

        foreach (var (job, turnToDispatch) in orderedTurns)
        {
            if (availableSlots <= 0) break;

            var turns = ParseQueuedTurns(job.QueuedTurnsJson);
            turns.RemoveAll(t => t.Id == turnToDispatch.Id);
            job.QueuedTurnsJson = JsonSerializer.Serialize(turns, JsonOpts);
            job.UpdatedAt = DateTimeOffset.UtcNow;

            var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == job.ProjectId);
            if (project == null) continue;

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

            var turnSessionId = Guid.NewGuid();

            var tasks = ParseTasks(job.TasksJson);
            var activeTask = tasks.FirstOrDefault(t => !t.IsCompleted);

            // Combine queued turn attachments with active task attachments so the agent gets both
            var combinedAttachments = new List<JobAttachmentDto>();
            if (turnToDispatch.Attachments is { Count: > 0 })
            {
                combinedAttachments.AddRange(turnToDispatch.Attachments);
            }
            if (activeTask?.Attachments is { Count: > 0 })
            {
                foreach (var att in activeTask.Attachments)
                {
                    if (!combinedAttachments.Any(a => a.Id == att.Id))
                    {
                        combinedAttachments.Add(att);
                    }
                }
            }

            if (combinedAttachments.Count > 0)
            {
                var sessionAttachDir = SessionsController.GetAttachmentDirectory(turnSessionId);
                var jobAttachDir = GetAttachmentDirectory(job.Id);

                foreach (var att in combinedAttachments)
                {
                    var safeName = SessionsController.SanitizeFileName(att.FileName);
                    var src = Path.Combine(jobAttachDir, $"{att.Id}_{safeName}");
                    if (!System.IO.File.Exists(src))
                    {
                        src = Directory.GetFiles(jobAttachDir, $"{att.Id}_*").FirstOrDefault() ??
                              Directory.GetFiles(jobAttachDir, $"*{att.Id}*").FirstOrDefault();
                    }
                    if (src != null && System.IO.File.Exists(src))
                    {
                        var dest = Path.Combine(sessionAttachDir, $"{att.Id}_{safeName}");
                        try { System.IO.File.Copy(src, dest, overwrite: true); } catch { }
                    }
                }
            }

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
                ["tier"] = persona.DefaultTier,
                ["contextSize"] = persona.DefaultContextSize,
                ["attachments"] = combinedAttachments
            };

            var turnSession = new AgentSession
            {
                Id = turnSessionId,
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

            var taskContext = FormatTaskContext(tasks);

            var promptWithContext = $"[Project Context: Project \"{project.Name}\" ({project.WorkspaceType}), Branch: \"{job.GitBranchName}\"]\n" +
                                    (!string.IsNullOrWhiteSpace(project.ProjectInstructions) ? $"[Project Guidelines: {project.ProjectInstructions}]\n" : "") +
                                    $"[Job Context: {job.Title} - {job.Description}]\n\n" +
                                    taskContext +
                                    turnToDispatch.Content;

            var structuredPayloadJson = combinedAttachments.Count > 0
                ? JsonSerializer.Serialize(new { attachments = combinedAttachments }, JsonOpts)
                : null;

            var promptMsg = new SessionMessage
            {
                SessionId = turnSession.Id,
                SenderAgentId = turnToDispatch.SenderType == "persona" ? (turnToDispatch.SenderId ?? "agent") : "operator",
                TargetAgentId = driverId,
                EventType = "prompt",
                Content = promptWithContext,
                StructuredPayloadJson = structuredPayloadJson,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.SessionMessages.Add(promptMsg);

            if (AgentMeshHub.IsAgentConnected(driverId))
            {
                driver.Status = "busy";
                driver.LastHeartbeatAt = DateTimeOffset.UtcNow;
            }

            var ackContent = activeTask != null
                ? (activeTask.RequiresDiscussion
                    ? $"I'm on it — starting discussion on \"{activeTask.Title}\" before coding."
                    : $"I'm on it — starting work on \"{activeTask.Title}\" now.")
                : "I'm on it — starting work on this now.";

            var ackMsg = new JobMessage
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                SenderType = "persona",
                SenderId = persona.Id,
                SenderName = persona.Name,
                Content = ackContent,
                MentionsJson = JsonSerializer.Serialize(new List<string> { persona.Id }, JsonOpts),
                TurnSessionId = turnSession.Id,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.JobMessages.Add(ackMsg);

            await db.SaveChangesAsync();

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
                    contextSize = persona.DefaultContextSize,
                    jobId = job.Id,
                    projectId = project.Id,
                    gitBranch = job.GitBranchName,
                    createdAt = promptMsg.CreatedAt,
                    attachments = combinedAttachments
                };

                var group = hubContext.Clients?.Group($"agent-{driverId}");
                if (group != null)
                {
                    await group.SendAsync("PromptReceived", promptPayload);
                }
                if (hubContext.Clients?.All != null)
                {
                    await hubContext.Clients.All.SendAsync("PromptReceived", promptPayload);
                    await hubContext.Clients.All.SendAsync("JobMessageCreated", ToMessageDto(ackMsg));
                    var msgCount = await db.JobMessages.CountAsync(m => m.JobId == job.Id);
                    var jobDto = ToDto(job, project.Name, msgCount);
                    await hubContext.Clients.All.SendAsync("JobUpdated", jobDto);
                }
            }

            availableSlots--;
        }
    }
}
