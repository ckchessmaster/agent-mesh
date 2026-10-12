using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
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
}
