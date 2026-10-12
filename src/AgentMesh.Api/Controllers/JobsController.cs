using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class JobsController(AgentMeshDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetJobs([FromQuery] string? approvalStatus)
    {
        var query = db.AgentJobs
            .AsNoTracking()
            .Include(j => j.RequestedByAgent)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(approvalStatus))
        {
            if (Enum.TryParse<ApprovalStatus>(approvalStatus, ignoreCase: true, out var parsedStatus))
            {
                query = query.Where(j => j.ApprovalStatus == parsedStatus);
            }
            else if (int.TryParse(approvalStatus, out var statusInt) && Enum.IsDefined(typeof(ApprovalStatus), statusInt))
            {
                query = query.Where(j => (int)j.ApprovalStatus == statusInt);
            }
        }

        var jobs = await query
            .OrderByDescending(j => j.CreatedAt)
            .Take(100)
            .ToListAsync();

        return Ok(jobs);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetJob(Guid id)
    {
        var job = await db.AgentJobs
            .AsNoTracking()
            .Include(j => j.RequestedByAgent)
            .FirstOrDefaultAsync(j => j.Id == id);

        return job == null ? NotFound() : Ok(job);
    }

    [HttpGet("{id:guid}/artifacts/{artifactName}")]
    public async Task<IActionResult> GetArtifact(
        Guid id,
        string artifactName,
        [FromServices] IStorageService storageService)
    {
        var job = await db.AgentJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id);
        if (job == null) return NotFound("Job not found.");

        string? s3Uri = null;
        if (artifactName.Equals("payload", StringComparison.OrdinalIgnoreCase) || artifactName.Equals("payload.json", StringComparison.OrdinalIgnoreCase))
        {
            s3Uri = job.PayloadS3Uri;
            if (string.IsNullOrEmpty(s3Uri))
            {
                return Content(job.PayloadJson, "application/json");
            }
        }
        else if (artifactName.Equals("result", StringComparison.OrdinalIgnoreCase) || artifactName.Equals("result.json", StringComparison.OrdinalIgnoreCase))
        {
            s3Uri = job.ResultS3Uri;
            if (string.IsNullOrEmpty(s3Uri) && job.ResultJson != null)
            {
                return Content(job.ResultJson, "application/json");
            }
        }

        if (string.IsNullOrEmpty(s3Uri))
        {
            return NotFound($"Artifact '{artifactName}' not recorded for job {id}.");
        }

        try
        {
            var stream = await storageService.DownloadStreamAsync(s3Uri);
            return File(stream, "application/octet-stream", artifactName);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Failed to download artifact from SeaweedFS S3", details = ex.Message });
        }
    }

    [HttpGet("{id:guid}/artifacts/presigned")]
    public async Task<IActionResult> GetPresignedUrl(
        Guid id,
        [FromQuery] string artifactName,
        [FromServices] IStorageService storageService)
    {
        var job = await db.AgentJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id);
        if (job == null) return NotFound("Job not found.");

        string? s3Uri = artifactName.StartsWith("payload", StringComparison.OrdinalIgnoreCase)
            ? job.PayloadS3Uri
            : job.ResultS3Uri;

        if (string.IsNullOrEmpty(s3Uri))
        {
            return NotFound($"No S3 artifact location registered for '{artifactName}'.");
        }

        var url = await storageService.GetPresignedUrlAsync(s3Uri, TimeSpan.FromMinutes(15));
        return Ok(new { url, s3_uri = s3Uri, expires_in_minutes = 15 });
    }
}
