using System.Text;
using System.Text.RegularExpressions;
using AgentMesh.Api.Hubs;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Controllers;

/// <summary>
/// Body for <c>POST /api/projects/{id}/documents</c> when the document is pasted text.
/// </summary>
/// <remarks>
/// A form field rather than a JSON body because one call has to be able to carry both kinds at once:
/// the dashboard's "add documents" flow lets a user paste notes and attach files in the same submit, and
/// two endpoints would force the client to sequence them and handle a half-success.
/// </remarks>
public record CreateProjectDocumentRequest(
    string? Title = null,
    string? Content = null
);

public record ProjectPersonaSummaryDto(
    string Id,
    string Name,
    string Role,
    string DriverId
);

public record ProjectSummaryDto(
    string Id,
    string Name,
    string Description,
    WorkspaceType WorkspaceType,
    string? GitRepositoryUrl,
    string GitBaseBranch,
    string? S3BucketName,
    string? S3PrefixKey,
    string ProjectInstructions,
    string? DefaultLeadPersonaId,
    string? DefaultLeadPersonaName,
    List<ProjectPersonaSummaryDto> AssignedPersonas,
    int JobCount,
    int ActiveJobCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public record CreateProjectRequest(
    string Name,
    string? Id = null,
    string? Description = null,
    WorkspaceType WorkspaceType = WorkspaceType.Git,
    string? GitRepositoryUrl = null,
    string? GitBaseBranch = null,
    string? S3BucketName = null,
    string? S3PrefixKey = null,
    string? ProjectInstructions = null,
    string? DefaultLeadPersonaId = null,
    List<string>? AllowedPersonaIds = null
);

public record UpdateProjectRequest(
    string? Name = null,
    string? Description = null,
    WorkspaceType? WorkspaceType = null,
    string? GitRepositoryUrl = null,
    string? GitBaseBranch = null,
    string? S3BucketName = null,
    string? S3PrefixKey = null,
    string? ProjectInstructions = null,
    string? DefaultLeadPersonaId = null,
    List<string>? AllowedPersonaIds = null
);

public record AssignPersonaRequest(
    string PersonaId
);

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ProjectsController(
    AgentMeshDbContext db,
    IHubContext<AgentMeshHub>? hubContext = null,
    IStorageService? storageService = null) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetProjects()
    {
        var projects = await db.Projects
            .AsNoTracking()
            .OrderByDescending(p => p.UpdatedAt)
            .ToListAsync();

        var personas = await db.Personas
            .AsNoTracking()
            .ToDictionaryAsync(p => p.Id, p => p, StringComparer.OrdinalIgnoreCase);

        var assignments = await db.ProjectPersonas
            .AsNoTracking()
            .ToListAsync();

        var assignmentsByProject = assignments
            .GroupBy(a => a.ProjectId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(a => a.PersonaId).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var jobStats = await db.ProjectJobs
            .AsNoTracking()
            .GroupBy(j => j.ProjectId)
            .Select(g => new
            {
                ProjectId = g.Key,
                Total = g.Count(),
                Active = g.Count(j => j.Status == ProjectJobStatus.Active)
            })
            .ToDictionaryAsync(x => x.ProjectId, x => (x.Total, x.Active), StringComparer.OrdinalIgnoreCase);

        var dtos = projects.Select(p =>
        {
            var pIds = assignmentsByProject.GetValueOrDefault(p.Id, new List<string>());
            var assignedPersonas = pIds
                .Select(id => personas.TryGetValue(id, out var per)
                    ? new ProjectPersonaSummaryDto(per.Id, per.Name, per.Role, per.DriverId)
                    : new ProjectPersonaSummaryDto(id, id, "Persona", "agy"))
                .ToList();

            personas.TryGetValue(p.DefaultLeadPersonaId ?? string.Empty, out var lead);
            var (totalJobs, activeJobs) = jobStats.GetValueOrDefault(p.Id, (0, 0));

            return ToDto(p, lead?.Name, assignedPersonas, totalJobs, activeJobs);
        }).ToList();

        return Ok(dtos);
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetProject(string id)
    {
        var normId = id.Trim().ToLowerInvariant();
        var project = await db.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == normId);

        if (project == null)
        {
            return NotFound(new { error = $"Project '{id}' not found." });
        }

        var assignedPersonaIds = await db.ProjectPersonas
            .AsNoTracking()
            .Where(pp => pp.ProjectId == normId)
            .Select(pp => pp.PersonaId)
            .ToListAsync();

        var personas = await db.Personas
            .AsNoTracking()
            .Where(p => assignedPersonaIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p, StringComparer.OrdinalIgnoreCase);

        var assignedPersonas = assignedPersonaIds
            .Select(pId => personas.TryGetValue(pId, out var per)
                ? new ProjectPersonaSummaryDto(per.Id, per.Name, per.Role, per.DriverId)
                : new ProjectPersonaSummaryDto(pId, pId, "Persona", "agy"))
            .ToList();

        Persona? lead = null;
        if (!string.IsNullOrWhiteSpace(project.DefaultLeadPersonaId))
        {
            lead = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == project.DefaultLeadPersonaId);
        }

        var totalJobs = await db.ProjectJobs.CountAsync(j => j.ProjectId == normId);
        var activeJobs = await db.ProjectJobs.CountAsync(j => j.ProjectId == normId && j.Status == ProjectJobStatus.Active);

        return Ok(ToDto(project, lead?.Name, assignedPersonas, totalJobs, activeJobs));
    }

    [HttpPost]
    public async Task<IActionResult> CreateProject([FromBody] CreateProjectRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
        {
            return BadRequest(new { error = "Project Name is required." });
        }

        var normalizedId = !string.IsNullOrWhiteSpace(req.Id)
            ? NormalizeSlug(req.Id)
            : NormalizeSlug(req.Name);

        if (string.IsNullOrWhiteSpace(normalizedId))
        {
            return BadRequest(new { error = "A valid Project Id or Name is required." });
        }

        var existing = await db.Projects.FirstOrDefaultAsync(p => p.Id == normalizedId);
        if (existing != null)
        {
            return Conflict(new { error = $"Project with Id '{normalizedId}' already exists." });
        }

        var project = new Project
        {
            Id = normalizedId,
            Name = req.Name.Trim(),
            Description = req.Description?.Trim() ?? string.Empty,
            WorkspaceType = req.WorkspaceType,
            GitRepositoryUrl = req.GitRepositoryUrl?.Trim(),
            GitBaseBranch = !string.IsNullOrWhiteSpace(req.GitBaseBranch) ? req.GitBaseBranch.Trim() : "main",
            S3BucketName = req.S3BucketName?.Trim(),
            S3PrefixKey = req.S3PrefixKey?.Trim(),
            ProjectInstructions = req.ProjectInstructions?.Trim() ?? string.Empty,
            DefaultLeadPersonaId = req.DefaultLeadPersonaId?.Trim().ToLowerInvariant(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.Projects.Add(project);

        var assignedDtos = new List<ProjectPersonaSummaryDto>();
        if (req.AllowedPersonaIds is { Count: > 0 })
        {
            var validPersonaIds = req.AllowedPersonaIds
                .Select(p => p.Trim().ToLowerInvariant())
                .Distinct()
                .ToList();

            var personas = await db.Personas
                .Where(p => validPersonaIds.Contains(p.Id))
                .ToListAsync();

            foreach (var p in personas)
            {
                db.ProjectPersonas.Add(new ProjectPersona
                {
                    ProjectId = project.Id,
                    PersonaId = p.Id,
                    AssignedAt = DateTimeOffset.UtcNow
                });
                assignedDtos.Add(new ProjectPersonaSummaryDto(p.Id, p.Name, p.Role, p.DriverId));
            }
        }

        await db.SaveChangesAsync();

        Persona? lead = null;
        if (!string.IsNullOrWhiteSpace(project.DefaultLeadPersonaId))
        {
            lead = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == project.DefaultLeadPersonaId);
        }

        var dto = ToDto(project, lead?.Name, assignedDtos, 0, 0);

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("ProjectCreated", dto);
        }

        return CreatedAtAction(nameof(GetProject), new { id = project.Id }, dto);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateProject(string id, [FromBody] UpdateProjectRequest req)
    {
        var normId = id.Trim().ToLowerInvariant();
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == normId);
        if (project == null)
        {
            return NotFound(new { error = $"Project '{id}' not found." });
        }

        if (!string.IsNullOrWhiteSpace(req.Name)) project.Name = req.Name.Trim();
        if (req.Description != null) project.Description = req.Description.Trim();
        if (req.WorkspaceType.HasValue) project.WorkspaceType = req.WorkspaceType.Value;
        if (req.GitRepositoryUrl != null) project.GitRepositoryUrl = req.GitRepositoryUrl.Trim();
        if (!string.IsNullOrWhiteSpace(req.GitBaseBranch)) project.GitBaseBranch = req.GitBaseBranch.Trim();
        if (req.S3BucketName != null) project.S3BucketName = req.S3BucketName.Trim();
        if (req.S3PrefixKey != null) project.S3PrefixKey = req.S3PrefixKey.Trim();
        if (req.ProjectInstructions != null) project.ProjectInstructions = req.ProjectInstructions.Trim();
        if (req.DefaultLeadPersonaId != null) project.DefaultLeadPersonaId = req.DefaultLeadPersonaId.Trim().ToLowerInvariant();

        if (req.AllowedPersonaIds != null)
        {
            var currentAssignments = await db.ProjectPersonas.Where(pp => pp.ProjectId == normId).ToListAsync();
            db.ProjectPersonas.RemoveRange(currentAssignments);

            var validIds = req.AllowedPersonaIds
                .Select(p => p.Trim().ToLowerInvariant())
                .Distinct()
                .ToList();

            var personas = await db.Personas
                .Where(p => validIds.Contains(p.Id))
                .ToListAsync();

            foreach (var p in personas)
            {
                db.ProjectPersonas.Add(new ProjectPersona
                {
                    ProjectId = project.Id,
                    PersonaId = p.Id,
                    AssignedAt = DateTimeOffset.UtcNow
                });
            }
        }

        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var assignedPersonaIds = await db.ProjectPersonas
            .AsNoTracking()
            .Where(pp => pp.ProjectId == normId)
            .Select(pp => pp.PersonaId)
            .ToListAsync();

        var personasMap = await db.Personas
            .AsNoTracking()
            .Where(p => assignedPersonaIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p, StringComparer.OrdinalIgnoreCase);

        var assignedPersonas = assignedPersonaIds
            .Select(pId => personasMap.TryGetValue(pId, out var per)
                ? new ProjectPersonaSummaryDto(per.Id, per.Name, per.Role, per.DriverId)
                : new ProjectPersonaSummaryDto(pId, pId, "Persona", "agy"))
            .ToList();

        Persona? lead = null;
        if (!string.IsNullOrWhiteSpace(project.DefaultLeadPersonaId))
        {
            lead = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == project.DefaultLeadPersonaId);
        }

        var totalJobs = await db.ProjectJobs.CountAsync(j => j.ProjectId == normId);
        var activeJobs = await db.ProjectJobs.CountAsync(j => j.ProjectId == normId && j.Status == ProjectJobStatus.Active);

        var dto = ToDto(project, lead?.Name, assignedPersonas, totalJobs, activeJobs);

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("ProjectUpdated", dto);
        }

        return Ok(dto);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteProject(string id)
    {
        var normId = id.Trim().ToLowerInvariant();
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == normId);
        if (project == null)
        {
            return NotFound(new { error = $"Project '{id}' not found." });
        }

        var assignments = await db.ProjectPersonas.Where(pp => pp.ProjectId == normId).ToListAsync();
        db.ProjectPersonas.RemoveRange(assignments);

        var jobs = await db.ProjectJobs.Where(j => j.ProjectId == normId).ToListAsync();
        var jobIds = jobs.Select(j => j.Id).ToList();

        if (jobIds.Count > 0)
        {
            var messages = await db.JobMessages.Where(m => jobIds.Contains(m.JobId)).ToListAsync();
            db.JobMessages.RemoveRange(messages);
            db.ProjectJobs.RemoveRange(jobs);
        }

        // The document library is a project child like the other three, and this schema has no foreign
        // keys — every parent deletes its children explicitly. Omitting it here would orphan rows that
        // are still readable by project slug, so a project deleted and re-created under the same id
        // would open with the previous project's specs attached to it. That is not a leak of deleted
        // bytes; it is the new project silently inheriting another project's context into every prompt.
        var documents = await db.ProjectDocuments.Where(d => d.ProjectId == normId).ToListAsync();
        if (documents.Count > 0)
        {
            db.ProjectDocuments.RemoveRange(documents);
        }

        db.Projects.Remove(project);
        await db.SaveChangesAsync();

        // The uploaded bytes live outside the database, so the row delete above does not reach them. Same
        // best-effort posture as DeleteProjectDocument: a directory that will not delete must not fail a
        // project deletion whose durable part already succeeded.
        if (documents.Count > 0)
        {
            TryDeleteDocumentDirectory(normId);
        }

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("ProjectDeleted", new { id = normId, deletedJobsCount = jobIds.Count });
        }

        return Ok(new { status = "deleted", id = normId, deletedJobsCount = jobIds.Count });
    }

    [HttpGet("{id}/personas")]
    [AllowAnonymous]
    public async Task<IActionResult> GetProjectPersonas(string id)
    {
        var normId = id.Trim().ToLowerInvariant();
        var assignedPersonaIds = await db.ProjectPersonas
            .AsNoTracking()
            .Where(pp => pp.ProjectId == normId)
            .Select(pp => pp.PersonaId)
            .ToListAsync();

        var personas = await db.Personas
            .AsNoTracking()
            .Where(p => assignedPersonaIds.Contains(p.Id))
            .ToListAsync();

        var dtos = personas
            .Select(p => new ProjectPersonaSummaryDto(p.Id, p.Name, p.Role, p.DriverId))
            .ToList();

        return Ok(dtos);
    }

    [HttpPost("{id}/personas")]
    public async Task<IActionResult> AssignPersona(string id, [FromBody] AssignPersonaRequest req)
    {
        var normId = id.Trim().ToLowerInvariant();
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == normId);
        if (project == null)
        {
            return NotFound(new { error = $"Project '{id}' not found." });
        }

        var normPersonaId = req.PersonaId.Trim().ToLowerInvariant();
        var persona = await db.Personas.FirstOrDefaultAsync(p => p.Id == normPersonaId);
        if (persona == null)
        {
            return NotFound(new { error = $"Persona '{req.PersonaId}' not found." });
        }

        var existing = await db.ProjectPersonas.FirstOrDefaultAsync(pp => pp.ProjectId == normId && pp.PersonaId == normPersonaId);
        if (existing == null)
        {
            db.ProjectPersonas.Add(new ProjectPersona
            {
                ProjectId = normId,
                PersonaId = normPersonaId,
                AssignedAt = DateTimeOffset.UtcNow
            });
            project.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var dto = new ProjectPersonaSummaryDto(persona.Id, persona.Name, persona.Role, persona.DriverId);

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("ProjectPersonaAssigned", new { projectId = normId, persona = dto });
        }

        return Ok(dto);
    }

    [HttpDelete("{id}/personas/{personaId}")]
    public async Task<IActionResult> RemovePersona(string id, string personaId)
    {
        var normId = id.Trim().ToLowerInvariant();
        var normPersonaId = personaId.Trim().ToLowerInvariant();

        var assignment = await db.ProjectPersonas.FirstOrDefaultAsync(pp => pp.ProjectId == normId && pp.PersonaId == normPersonaId);
        if (assignment == null)
        {
            return NotFound(new { error = $"Persona '{personaId}' is not assigned to project '{id}'." });
        }

        db.ProjectPersonas.Remove(assignment);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == normId);
        if (project != null)
        {
            if (string.Equals(project.DefaultLeadPersonaId, normPersonaId, StringComparison.OrdinalIgnoreCase))
            {
                project.DefaultLeadPersonaId = null;
            }
            project.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();

        if (hubContext != null)
        {
            await hubContext.Clients.All.SendAsync("ProjectPersonaRemoved", new { projectId = normId, personaId = normPersonaId });
        }

        return Ok(new { status = "removed", projectId = normId, personaId = normPersonaId });
    }

    private static ProjectSummaryDto ToDto(
        Project p,
        string? defaultLeadPersonaName,
        List<ProjectPersonaSummaryDto> assignedPersonas,
        int totalJobs,
        int activeJobs)
    {
        return new ProjectSummaryDto(
            p.Id,
            p.Name,
            p.Description,
            p.WorkspaceType,
            p.GitRepositoryUrl,
            p.GitBaseBranch,
            p.S3BucketName,
            p.S3PrefixKey,
            p.ProjectInstructions,
            p.DefaultLeadPersonaId,
            defaultLeadPersonaName,
            assignedPersonas,
            totalJobs,
            activeJobs,
            p.CreatedAt,
            p.UpdatedAt
        );
    }

    private static string NormalizeSlug(string input)
    {
        var cleaned = Regex.Replace(input.ToLowerInvariant().Trim(), @"[^a-z0-9\-_]+", "-");
        return cleaned.Trim('-');
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Supporting-document library
    //
    // Project-scope documents (specs, contracts, screenshots) that outlive any single job or message.
    // The validation, size caps, media-type guessing and filename sanitisation are SessionsController's
    // verbatim rather than a second copy: two allow-lists drift, and the drift shows up as a file the
    // dashboard accepts and the agent cannot open.
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pod-local home for a project's uploaded documents.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>ProjectJobsController.GetAttachmentDirectory</c> (<c>…/jobs/{id}/attachments</c>) with
    /// <c>projects/{projectId}/documents</c>. The <c>projects/</c> segment is what keeps a project id and
    /// a job id from colliding in the same namespace — both are caller-visible strings, and a job guid
    /// landing in a project's directory would be a file an operator cannot attribute.
    /// <para>
    /// Creates the directory on demand, like every other attachment path here. That is why this is not
    /// <c>Path.Combine</c> at the call site: a read that finds no directory returns "not found" rather
    /// than throwing, and the write path needs the directory to exist before the first
    /// <see cref="FileStream"/>.
    /// </para>
    /// </remarks>
    public static string GetDocumentDirectory(string projectId)
    {
        var normalized = (projectId ?? string.Empty).Trim().ToLowerInvariant();
        var basePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agentmesh",
            "artifacts",
            "projects",
            normalized.Length == 0 ? "_unknown" : normalized,
            "documents");

        Directory.CreateDirectory(basePath);
        return basePath;
    }

    /// <summary>
    /// Lists a project's document library: metadata only, no bodies.
    /// </summary>
    [HttpGet("{id}/documents")]
    [AllowAnonymous]
    public async Task<IActionResult> GetProjectDocuments(string id, CancellationToken ct)
    {
        var normId = id.Trim().ToLowerInvariant();
        if (await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == normId, ct) is null)
        {
            return NotFound(new { error = $"Project '{id}' not found." });
        }

        var documents = await db.LoadForProjectAsync(normId, ct: ct);

        return Ok(documents.Select(d => ProjectDocumentDto.From(d)).ToList());
    }

    /// <summary>
    /// Reads one document's metadata, including its body when it is a text document.
    /// </summary>
    [HttpGet("{id}/documents/{docId:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetProjectDocument(string id, Guid docId, CancellationToken ct)
    {
        var normId = id.Trim().ToLowerInvariant();
        var document = await db.LoadOneAsync(normId, docId, ct);
        if (document is null)
        {
            return NotFound(new { error = $"Document '{docId}' was not found for project '{normId}'." });
        }

        return Ok(ProjectDocumentDto.From(document, includeContent: true));
    }

    /// <summary>
    /// Adds documents to a project: pasted text, uploaded files, or both in one call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why one endpoint for both kinds.</strong> The dashboard's add-documents flow lets a user
    /// type notes and attach a screenshot in the same submit. Two endpoints would make the client
    /// sequence them and handle the case where the text saved and the file did not, which is a worse
    /// failure than one call that validates everything before writing anything.
    /// </para>
    /// <para>
    /// <strong>Why validation runs over every input before any write.</strong> A partial upload leaves a
    /// library that looks complete: the user attached four files, three landed, and nothing says which.
    /// So the loop below checks emptiness, size and type for all files first, then the count cap, then
    /// the text body, and only then touches the disk. A rejected request writes nothing.
    /// </para>
    /// <para>
    /// <strong>Why the count cap is checked before the bytes are written.</strong>
    /// <see cref="ProjectDocument.MaxDocumentsPerProject"/> is a property of the library, not of the
    /// request, so it is read from the database rather than trusted from the payload. Checking before
    /// writing means a rejected request leaves no orphaned file on disk and no row in the table.
    /// </para>
    /// <para>
    /// <strong>What that check does not give is atomicity, and that is a deliberate open question.</strong>
    /// This is a read-then-write with no unique constraint or serializable transaction behind it, so two
    /// concurrent uploads to a project sitting at 49 documents can both read 49 and both be admitted, and
    /// the library can end up a few rows over the cap. No other write path in this API opens a transaction,
    /// so adding one here would be the schema-adjacent decision rather than the local one.
    /// </para>
    /// <para>
    /// The consequence is bounded, which is why it is acceptable for now: the cap exists to limit what the
    /// prompt index has to carry, and the index is bounded independently by
    /// <see cref="ProjectDocumentIndexFormatter.MaxRenderedDocuments"/> plus an explicit "N more not
    /// listed" line. A small overshoot therefore degrades nothing a user can see. Closing the race for real
    /// needs either a serializable transaction around every upload or a per-project counter row with a
    /// check constraint — both worth an architect's call, not an implementation-side silent choice.
    /// </para>
    /// </remarks>
    [HttpPost("{id}/documents")]
    [RequestSizeLimit(262_144_000)]
    public async Task<IActionResult> CreateProjectDocuments(
        string id,
        [FromForm] IFormFileCollection? files = null,
        [FromForm] string? title = null,
        [FromForm] string? content = null,
        CancellationToken ct = default)
    {
        var normId = id.Trim().ToLowerInvariant();
        if (await db.Projects.FirstOrDefaultAsync(p => p.Id == normId, ct) is null)
        {
            return NotFound(new { error = $"Project '{id}' not found." });
        }

        // The parameter binding is the happy path, but the same form read through Request.Form is what
        // the existing attachment endpoints fall back to, and the controller tests construct the context
        // that way. Both are honoured so a caller cannot be rejected for supplying the files through the
        // channel the framework actually populated.
        var formFiles = files is { Count: > 0 }
            ? files
            : (Request?.HasFormContentType == true ? Request.Form.Files : null);

        var textBody = content?.Trim();
        var hasText = !string.IsNullOrWhiteSpace(textBody);

        if ((formFiles is null || formFiles.Count == 0) && !hasText)
        {
            return BadRequest(new { error = "Provide at least one file or a text document (title and content)." });
        }

        if (formFiles is { Count: > SessionsController.MaxAttachmentsPerMessage })
        {
            return BadRequest(new
            {
                error = $"A maximum of {SessionsController.MaxAttachmentsPerMessage} files can be uploaded per request."
            });
        }

        // Pass 1: validate every file before any byte is written. See the remarks for why this is a
        // separate pass rather than a check inside the write loop.
        if (formFiles is not null)
        {
            foreach (var file in formFiles)
            {
                if (file.Length <= 0)
                {
                    return BadRequest(new { error = $"File '{file.FileName}' is empty." });
                }

                if (file.Length > ProjectDocument.MaxDocumentSizeBytes)
                {
                    return BadRequest(new
                    {
                        error = $"File '{file.FileName}' exceeds the " +
                                $"{ProjectDocument.MaxDocumentSizeBytes / (1024 * 1024)} MB maximum file size limit."
                    });
                }

                if (!SessionsController.IsAllowedAttachment(file.FileName, file.ContentType))
                {
                    return BadRequest(new { error = $"File '{file.FileName}' has an unsupported file type." });
                }
            }
        }

        if (hasText && textBody!.Length > ProjectDocument.MaxTextContentChars)
        {
            return BadRequest(new
            {
                error = $"Text document is {textBody.Length} characters, over the " +
                        $"{ProjectDocument.MaxTextContentChars} character limit for an inline document. " +
                        "Upload it as a .md or .txt file instead."
            });
        }

        var incomingCount = (formFiles?.Count ?? 0) + (hasText ? 1 : 0);
        var existingCount = await db.CountForProjectAsync(normId, ct);
        if (existingCount + incomingCount > ProjectDocument.MaxDocumentsPerProject)
        {
            return BadRequest(new
            {
                error = $"This project can hold {ProjectDocument.MaxDocumentsPerProject} documents and already " +
                        $"has {existingCount}; this request would add {incomingCount}."
            });
        }

        var created = new List<ProjectDocument>();
        var documentDirectory = GetDocumentDirectory(normId);

        if (hasText)
        {
            // A text document's title is required in practice: an index line reading "Untitled document"
            // tells an agent nothing about whether to fetch it. Defaulting it to the first line is the
            // least-bad fallback, because the first line of pasted prose is usually its own heading.
            var resolvedTitle = FirstNonBlankLine(title) ?? FirstNonBlankLine(textBody) ?? "Pasted notes";
            if (resolvedTitle.Length > 255) resolvedTitle = resolvedTitle[..255];

            var textDocument = new ProjectDocument
            {
                Id = Guid.NewGuid(),
                ProjectId = normId,
                Title = resolvedTitle,
                Kind = ProjectDocumentKind.Text,
                Content = textBody!,
                SizeBytes = Encoding.UTF8.GetByteCount(textBody!),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            db.ProjectDocuments.Add(textDocument);
            created.Add(textDocument);
        }

        if (formFiles is not null)
        {
            foreach (var file in formFiles)
            {
                var documentId = Guid.NewGuid();
                var safeFileName = SessionsController.SanitizeFileName(file.FileName);
                var contentType = !string.IsNullOrWhiteSpace(file.ContentType) &&
                                  !string.Equals(file.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
                    ? file.ContentType.Trim()
                    : SessionsController.GuessContentTypeFromExtension(safeFileName);

                var document = new ProjectDocument
                {
                    Id = documentId,
                    ProjectId = normId,
                    Title = FileTitle(title, safeFileName),
                    Kind = ProjectDocumentKind.File,
                    FileName = safeFileName,
                    ContentType = contentType,
                    SizeBytes = file.Length,
                    // Set unconditionally, whether or not the mirror succeeds. The read path tries local
                    // first and falls back to this key, so a key recorded for an object that was never
                    // written costs one failed S3 GET on a pod-local read; a key NOT recorded for an
                    // object that WAS written means the file is unreachable from every other pod. This is
                    // the same ordering the job-attachment path uses.
                    S3Key = $"projects/{normId}/documents/{documentId:N}/{safeFileName}",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                await using (var localStream = new FileStream(
                    LocalDocumentPath(documentDirectory, documentId, safeFileName),
                    FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    await file.CopyToAsync(localStream, ct);
                }

                if (storageService is not null)
                {
                    try
                    {
                        await using var s3Stream = file.OpenReadStream();
                        await storageService.UploadStreamAsync(document.S3Key!, s3Stream, contentType, ct);
                    }
                    catch
                    {
                        // Graceful fallback to local disk if SeaweedFS S3 is unreachable — the same
                        // contract as UploadJobAttachments, and for the same reason: an object store being
                        // down must not lose a document the user just attached.
                    }
                }

                db.ProjectDocuments.Add(document);
                created.Add(document);
            }
        }

        await db.SaveChangesAsync(ct);

        if (hubContext is not null)
        {
            await hubContext.Clients.All.SendAsync(
                "ProjectDocumentsUpdated",
                new { id = normId, added = created.Count, documentIds = created.Select(d => d.Id.ToString("N")) },
                ct);
        }

        return Ok(created.Select(d => ProjectDocumentDto.From(d)).ToList());
    }

    /// <summary>
    /// Serves a document's bytes: local disk first, then the S3 mirror.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Local first, not S3 first.</strong> <c>SessionsController.GetAttachmentContent</c> tries
    /// S3 first; <c>ProjectJobsController.GetAttachmentContent</c> tries local first. This follows the job
    /// path because the local copy is the one that is always written (the S3 mirror is best-effort and
    /// silently skipped when the store is down), so leading with S3 makes an object-store outage take
    /// downloads with it. Leading with local makes the outage cost nothing on the pod that has the file.
    /// </para>
    /// <para>
    /// A text document has no bytes on disk, so it is served from the row as UTF-8 markdown/text. That is
    /// what makes a pasted spec fetchable by the same URL the index advertises for an upload — one URL
    /// shape for the agent to reason about, regardless of how the document got in.
    /// </para>
    /// </remarks>
    [HttpGet("{id}/documents/{docId:guid}/content")]
    [AllowAnonymous]
    public async Task<IActionResult> GetProjectDocumentContent(
        string id,
        Guid docId,
        [FromQuery] bool download = false,
        CancellationToken ct = default)
    {
        var normId = id.Trim().ToLowerInvariant();
        var document = await db.LoadOneAsync(normId, docId, ct);
        if (document is null)
        {
            return NotFound(new { error = $"Document '{docId}' was not found for project '{normId}'." });
        }

        var fileName = string.IsNullOrWhiteSpace(document.FileName)
            ? $"{(string.IsNullOrWhiteSpace(document.Title) ? "document" : SessionsController.SanitizeFileName(document.Title))}.md"
            : document.FileName;

        var contentType = !string.IsNullOrWhiteSpace(document.ContentType)
            ? document.ContentType
            : SessionsController.GuessContentTypeFromExtension(fileName);

        Stream contentStream;

        if (document.Kind == ProjectDocumentKind.Text)
        {
            // Served as markdown when the caller did not name a type: pasted instructions are markdown
            // in every case the dashboard produces, and text/markdown renders inline in the dashboard's
            // own viewer where text/plain would not.
            contentStream = new MemoryStream(Encoding.UTF8.GetBytes(document.Content ?? string.Empty));
            if (string.IsNullOrWhiteSpace(document.ContentType))
            {
                contentType = "text/markdown";
            }
        }
        else
        {
            var localPath = FindLocalDocumentFile(GetDocumentDirectory(normId), docId, fileName);
            if (localPath is not null)
            {
                contentStream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            else if (storageService is not null && !string.IsNullOrWhiteSpace(document.S3Key))
            {
                try
                {
                    contentStream = await storageService.DownloadStreamAsync(document.S3Key, ct);
                }
                catch
                {
                    return NotFound(new
                    {
                        error = $"Document '{fileName}' is not on this pod and could not be read from storage."
                    });
                }
            }
            else
            {
                return NotFound(new { error = $"Document content for '{fileName}' was not found." });
            }
        }

        // The same inline/attachment rule as both existing content endpoints: anything a browser can
        // render is served inline unless the caller asked for a download, and anything it cannot is an
        // attachment so a click never shows a wall of binary.
        var isInlineRenderable = !download && (
            contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(contentType, "text/markdown", StringComparison.OrdinalIgnoreCase) ||
            contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase));

        if (Response is not null)
        {
            Response.Headers.ContentDisposition =
                $"{(isInlineRenderable ? "inline" : "attachment")}; filename=\"{SessionsController.SanitizeFileName(fileName)}\"";
        }

        return File(contentStream, contentType);
    }

    /// <summary>
    /// Removes one document from a project's library.
    /// </summary>
    /// <remarks>
    /// Deletes the row and the pod-local file. The S3 object is deliberately left in place:
    /// <see cref="IStorageService"/> exposes no delete, and adding one is a change to a public contract
    /// that every storage implementation must honour — out of scope for a feature PR. The consequence is
    /// stated plainly: an orphaned object per deleted upload, under a key that names its project, which
    /// is reclaimable by a lifecycle rule or a sweep. The row is the source of truth for what is listed
    /// and what is staged into a turn, so a leftover object is invisible to every read path.
    /// </remarks>
    [HttpDelete("{id}/documents/{docId:guid}")]
    public async Task<IActionResult> DeleteProjectDocument(string id, Guid docId, CancellationToken ct)
    {
        var normId = id.Trim().ToLowerInvariant();
        if (await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == normId, ct) is null)
        {
            return NotFound(new { error = $"Project '{id}' not found." });
        }

        var document = await db.ProjectDocuments
            .FirstOrDefaultAsync(d => d.Id == docId && d.ProjectId == normId, ct);

        if (document is null)
        {
            return NotFound(new { error = $"Document '{docId}' was not found for project '{normId}'." });
        }

        db.ProjectDocuments.Remove(document);
        await db.SaveChangesAsync(ct);

        // Best-effort file removal after the row is gone. If the delete fails, the row is still gone and
        // the bytes are unreachable through every read path — an orphan, not a leak of a "deleted"
        // document. Doing it in the other order would leave a row pointing at a file that is not there.
        TryDeleteLocalDocumentFiles(GetDocumentDirectory(normId), docId);

        if (hubContext is not null)
        {
            await hubContext.Clients.All.SendAsync(
                "ProjectDocumentsUpdated",
                new { id = normId, removed = 1, documentId = docId.ToString("N") },
                ct);
        }

        return Ok(new { status = "deleted", id = docId.ToString("N"), projectId = normId });
    }

    /// <summary>
    /// The on-disk name for a document's bytes: <c>{docId:N}_{sanitizedFileName}</c>.
    /// </summary>
    /// <remarks>
    /// The id is the <c>N</c> format (no hyphens) so the prefix is a fixed 32 characters and the first
    /// underscore after it is unconditionally the separator. A hyphenated guid would put dashes in front
    /// of the underscore and make the "strip the prefix to recover the filename" trick used by the job
    /// content endpoint depend on where the dashes happen to fall.
    /// </remarks>
    internal static string LocalDocumentPath(string directory, Guid documentId, string safeFileName) =>
        Path.Combine(directory, $"{documentId:N}_{safeFileName}");

    /// <summary>
    /// Finds a document's local file, tolerating a filename that has been rewritten since upload.
    /// </summary>
    /// <remarks>
    /// The exact name is tried first; the id-prefix glob is the fallback for a file whose name was
    /// sanitised differently by an older build, or whose row was backfilled. Returns null when the
    /// directory does not exist at all, which is the normal state on a pod that never received this
    /// upload — that is why this does not use the creating helper.
    /// </remarks>
    internal static string? FindLocalDocumentFile(string directory, Guid documentId, string? safeFileName)
    {
        if (!Directory.Exists(directory)) return null;

        var exact = LocalDocumentPath(directory, documentId, safeFileName ?? string.Empty);
        if (System.IO.File.Exists(exact)) return exact;

        var prefixed = Directory.GetFiles(directory, $"{documentId:N}_*").FirstOrDefault();
        if (prefixed is not null) return prefixed;

        // Last resort: a row written before ids were formatted with "N", or a file copied in by hand.
        return Directory.GetFiles(directory, $"*{documentId}*").FirstOrDefault();
    }

    /// <summary>
    /// Removes one document's bytes from the project's document directory.
    /// </summary>
    private static void TryDeleteLocalDocumentFiles(string directory, Guid documentId)
    {
        if (!Directory.Exists(directory)) return;

        foreach (var pattern in new[] { $"{documentId:N}_*", $"*{documentId}*" })
        {
            foreach (var candidate in Directory.GetFiles(directory, pattern))
            {
                try
                {
                    System.IO.File.Delete(candidate);
                }
                catch
                {
                    // A locked or already-removed file must not fail the delete: the row is gone, which
                    // is the part the user asked for.
                }
            }
        }
    }

    /// <summary>
    /// Removes a whole project's document directory after the project itself is deleted.
    /// </summary>
    /// <remarks>
    /// The path is rebuilt from the slug rather than taken from the caller, and the delete is refused
    /// unless the resolved directory is still a descendant of the artifacts root. <c>GetDocumentDirectory</c>
    /// already normalizes the slug, but this is a recursive <see cref="Directory.Delete"/> on a path whose
    /// last segment originated as a route value — the one place where a normalization bug becomes data
    /// loss rather than a 404, so the check is cheap insurance against deleting a tree nobody asked to
    /// delete.
    /// </remarks>
    private static void TryDeleteDocumentDirectory(string normalizedProjectId)
    {
        try
        {
            var directory = GetDocumentDirectory(normalizedProjectId);
            var full = Path.GetFullPath(directory);

            // GetDocumentDirectory returns …/artifacts/projects/{slug}/documents, so the directory two
            // levels up must be exactly …/artifacts/projects. Checking the grandparent rather than doing a
            // prefix match rejects both a slug that somehow resolved upward and the shared projects root
            // itself, and it asserts "this is one project's document directory" instead of "this is
            // somewhere under our tree".
            //
            // Project slugs are already normalized twice (NormalizeSlug strips '/' and '.', and
            // GetDocumentDirectory lowercases and trims), so this is defence in depth against a recursive
            // delete on a path whose last segment originated as a route value — the one place where a
            // normalization bug becomes data loss rather than a 404.
            var projectsRoot = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".agentmesh", "artifacts", "projects"));

            var projectDirectory = Path.GetDirectoryName(full);
            var libraryRoot = projectDirectory is null ? null : Path.GetDirectoryName(projectDirectory);

            if (!string.Equals(libraryRoot, projectsRoot, StringComparison.Ordinal))
            {
                return;
            }

            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
        catch
        {
            // The rows are already gone; leftover bytes under a slug that no longer resolves to a project
            // are unreachable through every read path. Failing the project deletion would be worse.
        }
    }

    /// <summary>
    /// The title an uploaded file gets.
    /// </summary>
    /// <remarks>
    /// A caller-supplied title applies to the first file only, and the rest fall back to their own
    /// filenames. Applying one title to four files would produce a library where four index lines read
    /// identically and an agent cannot tell which one to fetch — the failure the title exists to prevent.
    /// </remarks>
    private static string FileTitle(string? requestTitle, string safeFileName)
    {
        var supplied = FirstNonBlankLine(requestTitle);
        var title = supplied ?? safeFileName;
        return title.Length > 255 ? title[..255] : title;
    }

    private static string? FirstNonBlankLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        foreach (var line in value.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed;
        }

        return null;
    }
}

