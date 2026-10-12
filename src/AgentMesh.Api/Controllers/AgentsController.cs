using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class AgentsController(AgentMeshDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAgents()
    {
        var agents = await db.Agents
            .AsNoTracking()
            .OrderBy(a => a.Name)
            .ToListAsync();
        return Ok(agents);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetAgent(string id)
    {
        var agent = await db.Agents
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id);

        return agent == null ? NotFound() : Ok(agent);
    }
}
