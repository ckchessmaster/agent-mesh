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
    IHubContext<AgentMeshHub>? hubContext = null) : ControllerBase
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

        db.Projects.Remove(project);
        await db.SaveChangesAsync();

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
}
