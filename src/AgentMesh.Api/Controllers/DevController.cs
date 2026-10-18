using AgentMesh.Api.Hubs;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DevController(AgentMeshDbContext db, IHubContext<AgentMeshHub> hubContext) : ControllerBase
{
    [HttpPost("reset-mesh")]
    public async Task<IActionResult> ResetMesh()
    {
        var messages = await db.SessionMessages.ToListAsync();
        db.SessionMessages.RemoveRange(messages);

        var jobs = await db.AgentJobs.ToListAsync();
        db.AgentJobs.RemoveRange(jobs);

        var sessions = await db.AgentSessions.ToListAsync();
        db.AgentSessions.RemoveRange(sessions);

        var agents = await db.Agents.ToListAsync();
        db.Agents.RemoveRange(agents);

        await db.SaveChangesAsync();

        await hubContext.Clients.All.SendAsync("MeshReset", new { timestamp = DateTimeOffset.UtcNow });

        return Ok(new
        {
            status = "reset",
            message = "All agents, sessions, and events have been purged.",
            timestamp = DateTimeOffset.UtcNow
        });
    }
}
