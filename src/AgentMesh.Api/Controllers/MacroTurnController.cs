using System.Security.Claims;
using AgentMesh.Api.Services;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Controllers;

/// <summary>
/// Asks the control plane to run one coding turn for a job, with a prompt the caller already
/// assembled.
/// </summary>
/// <param name="Prompt">
/// The complete prompt. The dispatcher prepends its own project/job envelope, so this is the body
/// the assembler produced rather than a bare instruction.
/// </param>
/// <param name="PersonaId">Who should work the turn. Required.</param>
/// <param name="TaskId">
/// Checklist item the turn belongs to. Optional, but it is what scopes the workspace lease and the
/// checkpoint to one task; omitting it writes the job-level row.
/// </param>
/// <param name="TurnSessionId">
/// Re-attach to an existing turn session instead of starting a new one. Set by the worker when its
/// activity was retried after a dispatch that timed out client-side but landed server-side — without
/// it, a retry would start a second agent on the same task while the first is still running.
/// </param>
/// <param name="Sender">Display name recorded as the prompt's author.</param>
/// <param name="PreflightContext">Extra lines appended after the envelope, ending in a newline.</param>
public record StartMacroTurnRequest(
    string Prompt,
    string PersonaId,
    string? TaskId = null,
    Guid? TurnSessionId = null,
    string? Sender = null,
    string? PreflightContext = null);

/// <summary>What a dispatch produced.</summary>
/// <param name="AlreadyRunning">
/// True when <paramref name="turnSessionId"/> was supplied and that session is still live, so nothing
/// new was dispatched and the caller should keep observing the session it already has.
/// </param>
public record StartMacroTurnResponse(
    Guid TurnSessionId,
    string DriverId,
    string PersonaId,
    DateTimeOffset DispatchedAt,
    bool AlreadyRunning);

/// <summary>
/// The macro-turn dispatch surface used by the Temporal worker.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this endpoint exists at all.</strong> The worker cannot call
/// <see cref="JobTurnDispatcher"/> directly: it lives in the API assembly, and referencing the API
/// from the worker would drag the whole ASP.NET surface (and a circular project reference, since the
/// API already references Core and Infrastructure) into a background host. The dispatcher is also the
/// <em>only</em> correct way to start a turn — it mints the turn session, stages attachments, resolves
/// the driver, wakes the persona container, acquires the workspace lease and broadcasts
/// <c>PromptReceived</c>. Reimplementing any subset of that in the worker is how a runner ends up
/// dispatched without a lease or without its container being up.
/// </para>
/// <para>
/// So the split is: <em>dispatch</em> goes over HTTP to the one component that owns it, and
/// <em>observation</em> happens against the database the worker already has a connection to. That
/// asymmetry is deliberate. Polling the API for turn state would add a second hop for information
/// that is one indexed read away, and it would make the worker's progress dependent on the API being
/// reachable — which is precisely the assumption that fails during a rollout, when the API is the
/// thing being restarted.
/// </para>
/// <para>
/// <strong>Separate controller, separate authorization.</strong> Job chat is deliberately permissive
/// because the Cockpit reads it; this endpoint starts compute and takes a lease, so it is
/// <c>[Authorize]</c> with no anonymous path. It is not merged into
/// <c>ProjectJobsController</c> (already ~2,000 lines) and it must not inherit that controller's
/// <c>[AllowAnonymous]</c> exceptions by adjacency.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
public class MacroTurnController(
    AgentMeshDbContext db,
    JobTurnDispatcher turns,
    IJobWorkspaceService? workspaces = null) : ControllerBase
{
    /// <summary>
    /// Dispatches one coding turn, or reports the one already running.
    /// </summary>
    /// <remarks>
    /// Idempotent on <paramref name="req"/>'s <c>TurnSessionId</c>, which is what makes the worker's
    /// retry path safe. The sequence it protects against is concrete: the worker POSTs, the API
    /// dispatches and commits the session, the response is lost, the activity times out, Temporal
    /// retries, and the retry POSTs again. Without the re-attach check that second POST would start a
    /// second agent on the same task, and both would push to the same branch.
    /// </remarks>
    [HttpPost("api/project-jobs/{jobId:guid}/macro-turns")]
    public async Task<IActionResult> StartMacroTurn(
        Guid jobId,
        [FromBody] StartMacroTurnRequest req,
        CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Prompt))
        {
            return BadRequest(new { error = "prompt is required." });
        }

        if (string.IsNullOrWhiteSpace(req.PersonaId))
        {
            return BadRequest(new { error = "personaId is required: a turn runs as a persona, not as an anonymous agent." });
        }

        var job = await db.ProjectJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return NotFound(new { error = $"Job '{jobId}' not found." });

        var project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == job.ProjectId, ct);
        if (project is null) return NotFound(new { error = $"Project '{job.ProjectId}' not found." });

        var persona = await db.Personas.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == req.PersonaId.Trim().ToLowerInvariant(), ct);
        if (persona is null)
        {
            return NotFound(new { error = $"Persona '{req.PersonaId}' not found." });
        }

        // ── Re-attach before dispatching ─────────────────────────────────────────
        if (req.TurnSessionId is { } existingId && existingId != Guid.Empty)
        {
            var existing = await db.AgentSessions.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == existingId, ct);

            if (existing is not null)
            {
                if (!MacroTurnBelongsToJob(existing.MetadataJson, jobId))
                {
                    // The caller named a session that exists but belongs to a different job. Accepting
                    // it would let a stale workflow id steer another job's runner, and the two would
                    // then fight over one branch. Refusing is the only safe answer, and the worker
                    // treats it as a hard stop rather than falling through to a fresh dispatch.
                    return BadRequest(new
                    {
                        error = $"Turn session '{existingId}' belongs to a different job.",
                        sessionJobId = ExtractJobId(existing.MetadataJson),
                    });
                }

                var stillLive = existing.Status is "thinking" or "busy" or "active" or "waiting_for_input";
                return Ok(new StartMacroTurnResponse(
                    existing.Id,
                    existing.DriverId ?? string.Empty,
                    existing.PersonaId ?? persona.Id,
                    existing.CreatedAt,
                    AlreadyRunning: stillLive));
            }
        }

        var dispatched = await turns.DispatchAsync(new JobTurnRequest(
            job,
            project,
            persona,
            req.Prompt,
            Sender: string.IsNullOrWhiteSpace(req.Sender) ? ResolveSender() : req.Sender.Trim(),
            PreflightContext: req.PreflightContext,
            // The macro turn carries no attachments: the assembler already folded the checklist item's
            // attachments into the prompt text, and re-staging them here would duplicate them into the
            // turn session's directory under a second id.
            Attachments: null,
            Hydration: null), ct);

        // The lease has to follow the task, not just the job. DispatchAsync takes the job-level lease;
        // re-acquiring on the task row here is what keeps two concurrent macro turns on two tasks of
        // one job from each seeing the other as a lease conflict.
        if (workspaces is not null && JobWorkspaceState.IsTaskScoped(req.TaskId))
        {
            await workspaces.AcquireLeaseAsync(
                jobId,
                dispatched.Driver.Id,
                persona.Id,
                JobWorkspaceState.NormalizeTaskId(req.TaskId),
                ct);
        }

        return Ok(new StartMacroTurnResponse(
            dispatched.Session.Id,
            dispatched.Driver.Id,
            persona.Id,
            dispatched.Session.CreatedAt,
            AlreadyRunning: false));
    }

    /// <summary>
    /// True when a session's metadata names this job.
    /// </summary>
    /// <remarks>
    /// A session with no <c>jobId</c> in its metadata is treated as <em>not</em> belonging, so a
    /// caller cannot re-attach its way into an unrelated interactive session. <c>JobTurnDispatcher</c>
    /// always writes <c>jobId</c>, so a session it created always matches.
    /// </remarks>
    internal static bool MacroTurnBelongsToJob(string? metadataJson, Guid jobId) =>
        Guid.TryParse(ExtractJobId(metadataJson), out var parsed) && parsed == jobId;

    private static string? ExtractJobId(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("jobId", out var value))
            {
                return null;
            }

            return value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : value.ToString();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The identity recorded as the prompt's author when the caller did not name one.
    /// </summary>
    /// <remarks>
    /// The authenticated principal rather than a constant: <c>SessionMessage.SenderAgentId</c> is what
    /// the Cockpit resolves an avatar from, and stamping every macro turn "operator" would make
    /// harness-initiated work indistinguishable from a human typing into the job chat — which is the
    /// opposite of what an audit trail is for.
    /// </remarks>
    private string ResolveSender() =>
        User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? User?.FindFirst("AgentId")?.Value
        ?? "temporal-worker";
}
