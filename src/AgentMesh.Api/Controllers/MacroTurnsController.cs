using System.Security.Claims;
using System.Text.Json;
using AgentMesh.Api.Services;
using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentMesh.Api.Controllers;

/// <summary>A review decision on the task's open pull request.</summary>
/// <param name="Approved">True to merge, false to send the work back for changes.</param>
/// <param name="Comments">Findings; injected into the next coding turn when not approved.</param>
/// <param name="PrNumber">
/// The pull request the decision is about. Optional, but when the job has more than one open PR it is
/// the only thing that says which one was reviewed.
/// </param>
/// <param name="ReviewerId">
/// Who is reviewing. Optional: defaults to the authenticated principal. It is settable because the
/// Cockpit posts on behalf of a signed-in human whose persona id is not in the auth claim.
/// </param>
public record SubmitPrReviewRequest(
    bool Approved,
    string? Comments = null,
    int? PrNumber = null,
    string? ReviewerId = null);

/// <summary>Request to put one checklist task under macro workflow orchestration.</summary>
public record StartTaskWorkflowRequest(
    string TaskId,
    string? AgentId = null,
    string? PersonaId = null,
    string? WorkspacePath = null,
    string? TargetBranch = null,
    string? TurnSessionId = null);

/// <summary>
/// The macro-orchestration surface: start a task workflow, read its status, and deliver a review
/// decision.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why these three live together.</strong> They are the operator's controls for one lifecycle:
/// start the run, watch it, decide on its output. Splitting them across controllers would scatter the
/// authorization rule that they share — all three act on an orchestrated task, so all three are
/// <c>[Authorize]</c> with no anonymous path, unlike the job-chat surface the Cockpit reads.
/// </para>
/// <para>
/// <strong>Why the review endpoint is here and not on the job-chat controller.</strong> A review
/// decision merges code. <c>ProjectJobsController</c> carries <c>[AllowAnonymous]</c> exceptions on
/// several read endpoints because the dashboard needs them, and a merge-capable write must not sit in a
/// file where the next person adds an anonymous attribute by adjacency.
/// </para>
/// </remarks>
[ApiController]
[Authorize]
public class MacroTurnsController(
    AgentMeshDbContext db,
    IWorkflowSignalRouter workflows,
    ILogger<MacroTurnsController>? logger = null) : ControllerBase
{
    /// <summary>
    /// Delivers a pull-request review decision into the workflow parked on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The self-review rule.</strong> The spec requires rejecting a review authored by the PR's
    /// own agent, and the reason it has to be enforced here rather than trusted is that the workflow
    /// merges on the strength of this signal. An author-supplied approval would merge its own code with
    /// no second pair of eyes, which is the exact bypass the two-man review gate exists to prevent — and
    /// it would look like a legitimate review in the workflow's history, because the signal carries no
    /// evidence of where it came from.
    /// </para>
    /// <para>
    /// The author is read from the durable record rather than from the request: <c>open_pull_request</c>
    /// writes a job-ledger row carrying the PR linkage and stamped with the opening persona as its
    /// sender. Trusting a caller-supplied "author" field would let any request name a different author
    /// and sidestep the check entirely.
    /// </para>
    /// <para>
    /// Comparison is on the resolved reviewer identity, and a reviewer who is neither a known persona nor
    /// the literal <c>operator</c> is refused. That is deliberate: an unrecognised identity cannot be
    /// shown to be a different human from the author, so admitting it would make the check
    /// bypassable-by-typo.
    /// </para>
    /// </remarks>
    [HttpPost("api/project-jobs/{jobId:guid}/reviews")]
    public async Task<IActionResult> SubmitReview(
        Guid jobId,
        [FromBody] SubmitPrReviewRequest req,
        CancellationToken ct)
    {
        if (req is null) return BadRequest(new { error = "A review body is required." });

        var job = await db.ProjectJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return NotFound(new { error = $"Job '{jobId}' not found." });

        var (authorPersona, prUrl, prNumber) = await FindOpenPullRequestAsync(jobId, req.PrNumber, ct);
        var assignedPersonas = await LoadProjectPersonasAsync(job.ProjectId, ct);

        var reviewer = ResolveReviewerIdentity(req.ReviewerId);

        if (string.IsNullOrWhiteSpace(reviewer))
        {
            return BadRequest(new
            {
                error = "Reviewer identity could not be resolved. Sign in, or pass reviewerId " +
                        "naming a persona assigned to this project, or 'operator' for the human.",
            });
        }

        // ── Self-review guard ────────────────────────────────────────────────────
        if (authorPersona is not null &&
            string.Equals(authorPersona, reviewer, StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogWarning(
                "Refused a self-review on job {JobId}: reviewer {Reviewer} is the author of PR {Pr}",
                jobId, reviewer, prUrl ?? prNumber?.ToString() ?? "(unknown)");

            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "self_review_not_allowed",
                message = $"'{reviewer}' opened this pull request and cannot approve its own merge. " +
                          "A review must come from a different persona or from the operator.",
                author = authorPersona,
                prUrl,
                prNumber,
            });
        }

        if (authorPersona is not null &&
            !assignedPersonas.Contains(reviewer) &&
            !string.Equals(reviewer, "operator", StringComparison.OrdinalIgnoreCase))
        {
            // The reviewer is not the author, but also not anybody the platform can verify exists.
            // Accepting it would let "sorrenn" (one character off) satisfy the gate.
            return BadRequest(new
            {
                error = "unknown_reviewer",
                message = $"'{reviewer}' is not a persona assigned to project '{job.ProjectId}' and is " +
                          "not 'operator'. A review that merges code has to be attributable to somebody " +
                          "real.",
                prUrl,
                prNumber,
            });
        }

        var delivery = await workflows.SendPrReviewAsync(
            jobId,
            new PrReviewSignal(
                ReviewerId: reviewer,
                IsApproved: req.Approved,
                ReviewComments: req.Comments,
                Timestamp: DateTimeOffset.UtcNow,
                PrNumber: req.PrNumber ?? prNumber),
            ct);

        if (!delivery.Delivered)
        {
            // 409 rather than 200: the decision is real and the caller needs to know it did not reach
            // the workflow. A 200 here would tell a reviewer their approval was acted on when nothing
            // is merging, which is how work sits unreviewed on a branch for a week.
            return Conflict(new
            {
                error = "review_not_delivered",
                delivery.Status,
                detail = delivery.Detail,
                workflowId = delivery.WorkflowId,
                prUrl,
                prNumber,
            });
        }

        return Ok(new
        {
            delivered = true,
            workflowId = delivery.WorkflowId,
            decision = req.Approved ? "approved" : "changes_requested",
            reviewer,
            prUrl,
            prNumber,
            nextStep = req.Approved
                ? "The workflow will merge the pull request and complete the task."
                : "The workflow will hand these comments to the next coding turn.",
        });
    }

    /// <summary>
    /// Puts one checklist task under macro workflow orchestration.
    /// </summary>
    /// <remarks>
    /// Explicit and operator-triggered by design. The existing dispatch paths (job chat, queued turns,
    /// handoff) are untouched, so a job cannot end up with both a SignalR-dispatched runner and a
    /// Temporal-orchestrated turn driving the same branch.
    /// </remarks>
    [HttpPost("api/project-jobs/{jobId:guid}/workflow/start")]
    public async Task<IActionResult> StartWorkflow(
        Guid jobId,
        [FromBody] StartTaskWorkflowRequest req,
        CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.TaskId))
        {
            return BadRequest(new { error = "taskId is required: the workflow checkpoints per task." });
        }

        var job = await db.ProjectJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return NotFound(new { error = $"Job '{jobId}' not found." });

        var project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == job.ProjectId, ct);
        if (project is null) return NotFound(new { error = $"Project '{job.ProjectId}' not found." });

        if (!Guid.TryParse(req.TaskId.Trim(), out var taskId))
        {
            return BadRequest(new { error = $"'{req.TaskId}' is not a task GUID. Read the checklist first." });
        }

        var tasks = ProjectJobsController.ParseTasks(job.TasksJson);
        if (tasks.All(t => t.Id != taskId))
        {
            return BadRequest(new
            {
                error = "task_not_found",
                available = tasks.Select(t => new { t.Id, t.Title, Status = t.EffectiveStatus }),
            });
        }

        var personaId = (req.PersonaId ?? tasks.FirstOrDefault(t => t.Id == taskId)?.AssignedPersonaId
                         ?? project.DefaultLeadPersonaId)?.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(personaId))
        {
            return BadRequest(new
            {
                error = "no_persona",
                message = "No persona to run the task as: the request named none, the task has no " +
                          "assignee, and the project has no default lead.",
            });
        }

        var persona = await db.Personas.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == personaId, ct);
        if (persona is null)
        {
            return NotFound(new { error = $"Persona '{personaId}' not found." });
        }

        var agentId = (req.AgentId ?? persona.DriverId ?? personaId).Trim().ToLowerInvariant();

        // The workspace is resolved once, here, and carried on the input for the workflow's whole run.
        // Re-resolving per turn would let an edit to projects.working_directory move a running task's
        // checkout between its own turns.
        var workspacePath = string.IsNullOrWhiteSpace(req.WorkspacePath)
            ? project.GetResolvedWorkingDirectory()
            : WorkspacePathResolver.Normalize(req.WorkspacePath.Trim());

        var input = new TaskExecutionInput(
            JobId: jobId.ToString(),
            TaskId: taskId.ToString(),
            AgentId: agentId,
            ProjectId: project.Id,
            WorkspacePath: workspacePath,
            TargetBranch: string.IsNullOrWhiteSpace(req.TargetBranch) ? job.GitBranchName : req.TargetBranch!,
            PersonaId: persona.Id,
            TurnSessionId: req.TurnSessionId);

        var result = await workflows.StartTaskWorkflowAsync(input, ct);

        if (!result.Started)
        {
            return Ok(new
            {
                started = false,
                alreadyRunning = result.AlreadyRunning,
                workflowId = result.WorkflowId,
                detail = result.Detail,
                hint = result.AlreadyRunning
                    ? "Query the workflow's status instead of starting another run; a second workflow on " +
                      "the same job would dispatch a second agent onto the same branch."
                    : null,
            });
        }

        return Accepted(new
        {
            started = true,
            workflowId = result.WorkflowId,
            jobId,
            taskId,
            agentId,
            projectId = project.Id,
            workspacePath,
            targetBranch = input.TargetBranch,
            personaId = persona.Id,
        });
    }

    /// <summary>The live status of a job's macro workflow.</summary>
    [HttpGet("api/project-jobs/{jobId:guid}/workflow/status")]
    public async Task<IActionResult> WorkflowStatus(
        Guid jobId,
        [FromQuery] string? taskId,
        CancellationToken ct)
    {
        var status = await workflows.GetStatusAsync(jobId, taskId, ct);

        if (status is null)
        {
            return NotFound(new
            {
                error = "no_workflow",
                workflowId = WorkflowSignalRouter.WorkflowIdFor(jobId),
                message = "This job has no macro task workflow reporting status. It is either not " +
                          "orchestrated yet (POST the start endpoint) or its run has closed.",
            });
        }

        return Ok(status);
    }

    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The persona that opened this job's pull request, with its URL and number.
    /// </summary>
    /// <remarks>
    /// Read from the ledger row <c>open_pull_request</c> writes, because that row is the durable record
    /// of the PR existing and of who asked for the review. Returns nulls when no such row exists, which
    /// the caller treats as "cannot prove a self-review" rather than as "definitely not one" — see the
    /// comment at the call site for why that asymmetry is the safe direction.
    /// </remarks>
    private async Task<(string? AuthorPersona, string? PrUrl, int? PrNumber)> FindOpenPullRequestAsync(
        Guid jobId,
        int? prNumber,
        CancellationToken ct)
    {
        var query = db.JobMessages
            .AsNoTracking()
            .Where(m => m.JobId == jobId && m.SenderType == "persona" && m.StructuredPayloadJson != null);

        if (prNumber is not null)
        {
            // Filtered in memory rather than translated to SQL: the linkage lives inside a jsonb blob and
            // the in-memory provider the tests run on cannot translate a json path, so a SQL-side filter
            // would make this the untested path. Bounded by the job's own message count.
            var rows = await query.OrderByDescending(m => m.CreatedAt).ToListAsync(ct);

            foreach (var row in rows)
            {
                if (!TryReadPrLinkage(row.StructuredPayloadJson, out var rowUrl, out var rowNumber)) continue;
                if (rowNumber != prNumber.Value) continue;

                return (row.SenderId, rowUrl, rowNumber);
            }

            return (null, null, prNumber);
        }

        // No number supplied: the most recent row that actually carries a PR linkage.
        //
        // Not "the most recent persona row with any structured payload". The ledger is append-only and
        // the persona keeps talking after it opens the PR — an attachment post, a status note, a
        // requiresReply prompt, anything that writes structured_payload. Taking the newest row and then
        // testing it for linkage therefore returns "author unknown" for almost every real job, and
        // "unknown" is the direction this guard deliberately does *not* block on (see the class
        // remarks). The combination silently disables the self-review check the moment the author sends
        // any later message — the exact bypass this endpoint exists to close.
        //
        // Filtered in memory for the same reason as the numbered branch above: the linkage lives inside
        // a jsonb blob, and a SQL-side json path would not translate on the in-memory provider the
        // tests run on. Bounded by the job's own message count.
        var candidates = await query.OrderByDescending(m => m.CreatedAt).ToListAsync(ct);

        foreach (var row in candidates)
        {
            if (!TryReadPrLinkage(row.StructuredPayloadJson, out var rowUrl, out var rowNumber)) continue;

            return (row.SenderId, rowUrl, rowNumber);
        }

        return (null, null, null);
    }

    private static bool TryReadPrLinkage(
        string? structuredPayloadJson,
        out string? prUrl,
        out int? prNumber)
    {
        prUrl = null;
        prNumber = null;
        if (string.IsNullOrWhiteSpace(structuredPayloadJson)) return false;

        try
        {
            using var doc = JsonDocument.Parse(structuredPayloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            if (doc.RootElement.TryGetProperty("prUrl", out var url) &&
                url.ValueKind == JsonValueKind.String)
            {
                prUrl = url.GetString();
            }

            if (doc.RootElement.TryGetProperty("prNumber", out var number) &&
                number.ValueKind == JsonValueKind.Number &&
                number.TryGetInt32(out var parsed))
            {
                prNumber = parsed;
            }

            return prUrl is not null || prNumber is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Who is reviewing, from the request or the authenticated principal.
    /// </summary>
    /// <remarks>
    /// An explicit <c>reviewerId</c> wins because the Cockpit posts on behalf of a signed-in human whose
    /// persona id is not in the auth claim — the claim identifies the API key or the session, not the
    /// persona. The value is normalized the way every other persona id in this system is (trim,
    /// lowercase), because persona ids are stored lowercased and a case-sensitive comparison against a
    /// differently-cased input is how a self-review check passes.
    /// </remarks>
    private string? ResolveReviewerIdentity(string? requestedReviewer)
    {
        if (!string.IsNullOrWhiteSpace(requestedReviewer))
        {
            return requestedReviewer.Trim().ToLowerInvariant();
        }

        var claim = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? User?.FindFirst("AgentId")?.Value
                    ?? User?.FindFirst("PersonaId")?.Value;

        return string.IsNullOrWhiteSpace(claim) ? null : claim.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// The personas assigned to a project, lowercased.
    /// </summary>
    /// <remarks>
    /// Returned as a set rather than queried per check, and passed explicitly rather than cached on a
    /// field: controllers are instantiated per request but the compiler cannot prove that, and mutable
    /// instance state on a controller is the kind of thing that becomes a cross-request leak the first
    /// time someone marks it pooled.
    /// </remarks>
    private async Task<HashSet<string>> LoadProjectPersonasAsync(string projectId, CancellationToken ct)
    {
        var ids = await db.ProjectPersonas.AsNoTracking()
            .Where(pp => pp.ProjectId == projectId)
            .Select(pp => pp.PersonaId)
            .ToListAsync(ct);

        return new HashSet<string>(
            ids.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim().ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);
    }
}
