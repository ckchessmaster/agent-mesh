using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SessionsController(AgentMeshDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetSessions()
    {
        var sessions = await db.AgentSessions
            .OrderByDescending(s => s.CreatedAt)
            .Take(50)
            .ToListAsync();
        return Ok(sessions);
    }

    [HttpPost]
    public async Task<IActionResult> CreateSession([FromBody] CreateSessionRequest req)
    {
        var session = new AgentSession
        {
            Title = req.Title,
            Status = "active",
            MetadataJson = req.MetadataJson ?? "{}"
        };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetSessions), new { id = session.Id }, session);
    }
}

public record CreateSessionRequest(string Title, string? MetadataJson);
