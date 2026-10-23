using System.Security.Claims;
using AgentMesh.Api.Hubs;
using AgentMesh.Api.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Controllers;

public record SaveJobWorkspaceRequest(
    /// <summary>Runner reporting the save. Defaults to the authenticated agent.</summary>
    string? AgentId = null,
    string? PersonaId = null,
    Guid? TurnSessionId = null,
    string? HeadCommit = null,
    string? ContentHash = null,
    string? GitRemoteUrl = null,
    string? GitRef = null,
    string? BundleS3Key = null,
    bool ReleaseLease = false,
    /// <summary>Set by the runner once commit+push succeeded, to open the handoff gate.</summary>
    bool ConfirmHandoff = false,
    string? HandoffNote = null);

public record HandoffStatusDto(JobHandoffDto? Handoff, WorkspaceSnapshotDto? Workspace);

public record WorkspaceSnapshotDto(
    string? GitRemoteUrl,
    string? GitRef,
    string? HeadCommit,
    string? ContentHash,
    string? BundleS3Key,
    bool HasDurableState,
    string? LeaseHolderAgentId,
    string? LeaseHolderPersonaId,
    DateTimeOffset? LastSavedAt);

public record ConfirmHandoffRequest(string? Note = null);

/// <summary>
/// Where a job's work is saved, and where the handoff gate is operated.
///
/// Split from <c>ProjectJobsController</c> deliberately: job chat is an operator surface and is
/// largely <c>AllowAnonymous</c> for the dashboard, while these endpoints move bytes and release
/// locks. Keeping them together would mean either loosening the wrong half or locking out the
/// dashboard from the job board.
/// </summary>
[ApiController]
[Authorize]
public class JobWorkspaceController(
    AgentMeshDbContext db,
    IJobWorkspaceService workspaces,
    IJobHandoffService handoffs,
    IHubContext<AgentMeshHub>? hubContext = null) : ControllerBase
{
    /// <summary>
    /// What the runner should hydrate before it starts writing. Read by every turn dispatch on the
    /// runner side, so it has to stay cheap and never 404 on a fresh job.
    /// </summary>
    [HttpGet("api/project-jobs/{id:guid}/workspace")]
    [AllowAnonymous]
    public async Task<IActionResult> GetWorkspace(Guid id, CancellationToken ct)
    {
        var job = await db.ProjectJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job == null)
        {
            return NotFound(new { error = $"Job '{id}' not found." });
        }

        var state = await workspaces.GetAsync(id, ct);
        var handoff = await handoffs.GetDtoAsync(id, ct);

        return Ok(new HandoffStatusDto(handoff, ToDto(state, job)));
    }

    /// <summary>
    /// Persist the job's work so the next persona can get it, on this machine or another.
    /// Called by the runner after each turn; <paramref name="confirmHandoff"/> also opens the gate.
    /// </summary>
    [HttpPost("api/project-jobs/{id:guid}/workspace/save")]
    public async Task<IActionResult> SaveWorkspace(
        Guid id, [FromBody] SaveJobWorkspaceRequest req, CancellationToken ct)
    {
        var callerAgent = CallerAgentId();
        var agentId = !string.IsNullOrWhiteSpace(req.AgentId) ? req.AgentId.Trim() : callerAgent;

        if (string.IsNullOrWhiteSpace(agentId))
        {
            return BadRequest(new { error = "agentId is required (authenticate as an agent or pass one)." });
        }

        // A runner may only ever speak for itself. Without this, any authenticated agent could
        // rewrite the workspace provenance of another machine's checkout.
        if (callerAgent != null && !IsHumanOperator() &&
            !string.Equals(callerAgent, agentId, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        var result = await workspaces.SaveAsync(new SaveWorkspaceRequest(
            id, agentId!, req.PersonaId, req.TurnSessionId, req.HeadCommit, req.ContentHash,
            req.GitRemoteUrl, req.GitRef, req.BundleS3Key, req.ReleaseLease), ct);

        if (!result.Accepted)
        {
            return NotFound(new { error = result.Warning });
        }

        var handoffResult = (object?)null;
        if (req.ConfirmHandoff)
        {
            var decision = await handoffs.ConfirmAsync(
                id, agentId!, req.HandoffNote,
                operatorOverride: false, turnSessionId: req.TurnSessionId, ct: ct);

            if (!decision.Accepted)
            {
                // The save landed but the gate did not open — report both halves honestly rather
                // than pretending the whole call failed.
                return StatusCode(StatusCodes.Status409Conflict, new
                {
                    saved = true,
                    snapshot = ToDto(result.Snapshot),
                    warning = result.Warning,
                    code = decision.Code?.ToString(),
                    error = decision.Reason
                });
            }

            handoffResult = decision.Handoff == null
                ? new { status = "nothing_pending", reason = decision.Reason }
                : (object)new
                {
                    status = decision.Code?.ToString() ?? "released",
                    handoff = ToDto(decision.Handoff)
                };
        }

        return Ok(new
        {
            saved = true,
            snapshot = ToDto(result.Snapshot),
            warning = result.Warning,
            handoff = handoffResult
        });
    }

    /// <summary>Operator "hand over now": releases the gate without waiting for the outgoing agent.</summary>
    [HttpPost("api/project-jobs/{id:guid}/handoff/confirm")]
    public async Task<IActionResult> ConfirmHandoff(Guid id, [FromBody] ConfirmHandoffRequest? req, CancellationToken ct)
    {
        var open = await handoffs.GetOpenAsync(id, ct);
        if (open == null)
        {
            return NotFound(new { error = "No handoff is pending for this job." });
        }

        if (!await CanOverrideAsync(open, ct))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "Only an operator may force a handoff. The outgoing agent confirms via workspace/save."
            });
        }

        var decision = await handoffs.ConfirmAsync(
            id, CallerAgentId() ?? "operator", req?.Note,
            operatorOverride: true, ct: ct);
        if (decision.Handoff == null)
        {
            // Only reachable if the gate closed between the check above and here.
            return Conflict(new { error = decision.Reason });
        }

        return Ok(new { released = decision.Accepted, handoff = ToDto(decision.Handoff), reason = decision.Reason });
    }

    /// <summary>
    /// Escape hatch for a runner that died holding the lease. Drops the lock, keeps the work:
    /// without this, a killed agent strands its jobs until someone edits the database by hand.
    /// </summary>
    [HttpPost("api/agents/{agentId}/workspace/release")]
    public async Task<IActionResult> ReleaseAgentLeases(string agentId, CancellationToken ct)
    {
        if (!IsHumanOperator())
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Only an operator may release another agent's leases." });
        }

        var freed = await workspaces.ReleaseLeasesForAgentAsync(agentId.Trim().ToLowerInvariant(), ct);
        return Ok(new { agentId, releasedJobs = freed ?? Array.Empty<Guid>() });
    }

    /// <summary>Cancel a pending handoff and leave the current holder in place.</summary>
    [HttpPost("api/project-jobs/{id:guid}/handoff/cancel")]
    public async Task<IActionResult> CancelHandoff(Guid id, CancellationToken ct)
    {
        if (!IsHumanOperator())
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Only an operator may cancel a handoff." });
        }

        var open = await handoffs.GetOpenAsync(id, ct);
        if (open == null)
        {
            return NotFound(new { error = "No handoff is pending for this job." });
        }

        await handoffs.CancelAsync(id, $"Cancelled by operator {CallerAgentId() ?? "unknown"}.", ct);
        return Ok(new { cancelled = true, jobId = id });
    }

    /// <summary>
    /// Drain expired grace windows on demand. The reaper does this every few seconds; this exists
    /// so tests and operators do not have to wait for the sweep.
    /// </summary>
    [HttpPost("api/project-jobs/handoffs/expire")]
    public async Task<IActionResult> ExpireHandoffs(CancellationToken ct)
    {
        if (!IsHumanOperator())
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Only an operator may sweep handoffs." });
        }

        return Ok(new { released = await handoffs.ExpirePendingAsync(ct) });
    }

    private string? CallerAgentId()
    {
        var agentId = User?.FindFirst("AgentId")?.Value;
        return string.IsNullOrWhiteSpace(agentId) || agentId.Equals("operator", StringComparison.OrdinalIgnoreCase)
            ? null
            : agentId;
    }

    /// <summary>
    /// Humans (OIDC roles or a LeadHuman tier) may override a handoff. Runners may not — a driver
    /// auto-created for a persona lands at SeniorManager, so it cannot promote itself.
    ///
    /// The static master key is accepted as operator-equivalent: it is what the dashboard sends when
    /// nobody has logged in, and runners are configured with per-agent tokens so they authenticate
    /// through the agent-hash path instead. That is a convention, not a proof — a runner with no
    /// token configured falls back to the same master key. Hence <see cref="CanOverrideAsync"/> also
    /// refuses the two parties to the handoff, which are the actors actually positioned to abuse it.
    /// Closing the rest needs per-agent tokens everywhere: an Astra/Sorrn call, not a handoff PR.
    /// </summary>
    private bool IsHumanOperator()
    {
        if (User?.HasClaim(ClaimTypes.Role, "Operator") == true ||
            User?.HasClaim(ClaimTypes.Role, "Admin") == true ||
            User?.HasClaim("IsMasterKey", "true") == true)
        {
            return true;
        }

        return User?.FindFirst("TrustTier")?.Value == ((int)TrustTier.LeadHuman).ToString();
    }

    private async Task<bool> CanOverrideAsync(JobHandoff handoff, CancellationToken ct)
    {
        if (!IsHumanOperator()) return false;

        var caller = CallerAgentId();
        if (string.IsNullOrWhiteSpace(caller)) return true;

        // A party to the handoff must use its own path: the outgoing agent confirms through
        // /workspace/save, the incoming agent gets no release authority at all.
        var partyDriverIds = await db.Personas.AsNoTracking()
            .Where(p => p.Id == handoff.FromPersonaId || p.Id == handoff.ToPersonaId)
            .Select(p => p.DriverId)
            .ToListAsync(ct);

        return !partyDriverIds.Any(d => string.Equals(d?.Trim(), caller, StringComparison.OrdinalIgnoreCase));
    }

    private static WorkspaceSnapshotDto? ToDto(JobWorkspaceState? state, ProjectJob? job = null)
    {
        if (state == null)
        {
            // No save yet: point the runner at the project's own remote/branch so it can still clone.
            return job == null
                ? null
                : new WorkspaceSnapshotDto(null, job.GitBranchName, null, null, null, false, null, null, null);
        }

        return new WorkspaceSnapshotDto(
            state.GitRemoteUrl, state.GitRef, state.HeadCommit, state.ContentHash, state.BundleS3Key,
            !string.IsNullOrWhiteSpace(state.HeadCommit) || !string.IsNullOrWhiteSpace(state.ContentHash)
            || !string.IsNullOrWhiteSpace(state.BundleS3Key),
            state.LeaseHolderAgentId, state.LeaseHolderPersonaId, state.LastSavedAt);
    }

    private static WorkspaceSnapshotDto ToDto(WorkspaceSnapshot s) =>
        new(s.GitRemoteUrl, s.GitRef, s.HeadCommit, s.ContentHash, s.BundleS3Key, s.HasDurableState, null, null, null);

    private static JobHandoffDto ToDto(JobHandoff h) => new(
        h.Id, h.JobId, h.ProjectId, h.FromPersonaId, h.ToPersonaId, h.Status,
        h.ReleaseReason?.ToString(), h.HandoffNote, h.ExpectedHeadCommit,
        h.FromTurnSessionId, h.ToTurnSessionId, h.DeclaredAt, h.ReleaseDeadlineAt,
        h.ReleasedAt, h.CompletedAt, h.Error);
}
