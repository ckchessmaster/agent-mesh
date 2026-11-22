using AgentMesh.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentMesh.Api.Controllers;

/// <summary>
/// <c>GET /api/attention</c> — the cross-project inbox of everything waiting on a human.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists as its own endpoint.</strong> The Cockpit already renders job lists, but
/// only one project at a time: <c>GET /api/project-jobs</c> is scoped by project id and there is no
/// cross-project job-list route. Answering "what needs me?" therefore meant one request per project
/// plus the client re-implementing the wait rules — plan-approval heartbeats, pending cards,
/// unanswered escalations — which is exactly the logic
/// <see cref="AttentionQueryService"/> owns. This endpoint keeps the rules on the server so a
/// notification client and the bell cannot disagree about what counts as blocked.
/// </para>
/// <para>
/// <strong>Read-only, and deliberately so.</strong> There is no dismiss or acknowledge verb. A wait
/// ends when the underlying state changes (approval flips the card and the heartbeat, a review merges
/// the PR and completes the job), so an acknowledgement row would be a second, drift-prone source of
/// truth for something the workflow already knows. Client-side "seen" bookkeeping is enough for a
/// notification bell, and it survives a server restart without needing a schema.
/// </para>
/// <para>
/// <strong>Authentication.</strong> <c>[Authorize]</c>, matching
/// <see cref="ProjectsController"/>, because this is a cross-project read of titles, heartbeats and
/// escalation bodies — a strictly wider surface than the per-project endpoints that carry
/// <c>[AllowAnonymous]</c> exceptions for dashboard-on-load. The Cockpit's <c>apiFetch</c> attaches
/// the bearer header on every call, so this needs no anonymous path.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[Route("api/attention")]
public class AttentionController(IAttentionQueryService attention) : ControllerBase
{
    /// <summary>
    /// Every open wait across every project, oldest first. Empty rather than 404 when nothing is
    /// blocked, so a polling client has one shape to handle.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(AttentionRollupDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRollup(CancellationToken cancellationToken)
        => Ok(await attention.QueryAsync(cancellationToken));
}
