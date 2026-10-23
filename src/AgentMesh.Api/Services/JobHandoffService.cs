using AgentMesh.Api.Hubs;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentMesh.Api.Services;

public class JobHandoffOptions
{
    public const string SectionName = "JobHandoff";

    /// <summary>How long the incoming agent waits for the outgoing agent to confirm before being released anyway.</summary>
    public int GraceSeconds { get; set; } = 180;

    /// <summary>How often the reaper sweeps for expired grace windows.</summary>
    public int SweepSeconds { get; set; } = 5;

    /// <summary>Consecutive persona messages before we stop auto-hopping and wait for a human.</summary>
    public int MaxConsecutivePersonaHops { get; set; } = 5;

    public TimeSpan Grace => TimeSpan.FromSeconds(Math.Max(15, GraceSeconds));
    public TimeSpan SweepInterval => TimeSpan.FromSeconds(Math.Max(1, SweepSeconds));
}

public class JobHandoffService(
    AgentMeshDbContext db,
    JobTurnDispatcher turns,
    IJobWorkspaceService workspaces,
    IHubContext<AgentMeshHub>? hubContext = null,
    IOptions<JobHandoffOptions>? options = null,
    ILogger<JobHandoffService>? logger = null) : IJobHandoffService
{
    private readonly JobHandoffOptions Options = options?.Value ?? new JobHandoffOptions();

    public async Task<HandoffDecision> DeclareAsync(DeclareHandoffRequest req, CancellationToken ct = default)
    {
        if (string.Equals(req.FromPersonaId, req.ToPersonaId, StringComparison.OrdinalIgnoreCase))
        {
            return new HandoffDecision(false, null, "Cannot hand a job over to the persona already holding it.");
        }

        var job = await db.ProjectJobs.FirstOrDefaultAsync(j => j.Id == req.JobId, ct);
        if (job == null)
        {
            return new HandoffDecision(false, null, $"Job '{req.JobId}' not found.");
        }
        if (job.Status == ProjectJobStatus.Archived)
        {
            return new HandoffDecision(false, null, "Job is archived.");
        }

        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == job.ProjectId, ct);
        if (project == null)
        {
            return new HandoffDecision(false, null, $"Project '{job.ProjectId}' not found.");
        }

        var toPersona = await db.Personas.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == req.ToPersonaId, ct);
        if (toPersona == null)
        {
            return new HandoffDecision(false, null, $"Persona '{req.ToPersonaId}' not found.");
        }

        var existing = await GetOpenEntityAsync(req.JobId, ct);
        if (existing != null)
        {
            // Re-declaring to the same persona is idempotent — agents repeat themselves under
            // retries and a duplicate handoff row would strand the job in a second gate.
            if (string.Equals(existing.ToPersonaId, req.ToPersonaId, StringComparison.OrdinalIgnoreCase))
            {
                return new HandoffDecision(true, existing, "Handoff already pending for this persona.");
            }

            // A third party mentioned mid-flight wins the handoff; the previous gate is dropped.
            existing.Status = JobHandoffStatus.Cancelled;
            existing.Error = $"Superseded by handoff to @{req.ToPersonaId}.";
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        var now = DateTimeOffset.UtcNow;
        var handoff = new JobHandoff
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            ProjectId = project.Id,
            FromPersonaId = req.FromPersonaId,
            ToPersonaId = req.ToPersonaId,
            FromTurnSessionId = req.FromTurnSessionId,
            Status = JobHandoffStatus.Pending,
            DeclaredAt = now,
            ReleaseDeadlineAt = now + Options.Grace,
            UpdatedAt = now
        };
        db.JobHandoffs.Add(handoff);

        var fromName = await DisplayNameAsync(req.FromPersonaId, ct);
        var toName = await DisplayNameAsync(req.ToPersonaId, ct);

        var notice = new JobMessage
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            SenderType = "system",
            SenderId = "handoff",
            SenderName = "Handoff",
            Content = $"@{fromName} is handing this job to @{toName}. Waiting for @{fromName} to save " +
                      $"the workspace and confirm — up to {(int)Options.Grace.TotalSeconds}s before it " +
                      "proceeds without confirmation.",
            MentionsJson = "[]",
            TurnSessionId = req.FromTurnSessionId,
            CreatedAt = now
        };
        db.JobMessages.Add(notice);

        await db.SaveChangesAsync(ct);

        await PublishHandoffAsync(job.Id, handoff, notice, ct);

        logger?.LogInformation(
            "Handoff {HandoffId} declared on job {JobId}: @{From} -> @{To}, deadline {Deadline}",
            handoff.Id, job.Id, req.FromPersonaId, req.ToPersonaId, handoff.ReleaseDeadlineAt);

        return new HandoffDecision(true, handoff, null);
    }

    public async Task<JobHandoff?> GetOpenAsync(Guid jobId, CancellationToken ct = default) =>
        await GetOpenEntityAsync(jobId, ct);

    public async Task<HandoffDecision> ConfirmAsync(
        Guid jobId,
        string agentId,
        string? note = null,
        bool operatorOverride = false,
        Guid? turnSessionId = null,
        CancellationToken ct = default)
    {
        var handoff = await GetOpenEntityAsync(jobId, ct);
        if (handoff == null)
        {
            // Normal case: an ordinary turn that did not hand anything over. The runner saves and
            // confirms on every turn, so "nothing pending" must not look like a failure.
            return new HandoffDecision(true, null, "No handoff is pending for this job.", HandoffConfirmCode.NothingPending);
        }
        if (handoff.Status != JobHandoffStatus.Pending)
        {
            return new HandoffDecision(true, handoff, "Handoff already released.", HandoffConfirmCode.AlreadyReleased);
        }

        if (!operatorOverride)
        {
            // Only the outgoing persona's own runner may confirm. Otherwise an incoming agent could
            // release itself early and walk into a workspace that is still being written.
            var expectedDriver = await DriverOfAsync(handoff.FromPersonaId, ct);
            if (expectedDriver != null &&
                !string.Equals(expectedDriver, agentId, StringComparison.OrdinalIgnoreCase))
            {
                return new HandoffDecision(false, handoff,
                    $"Only the outgoing agent '@{expectedDriver}' (or an operator) can confirm this handoff.",
                    HandoffConfirmCode.NotOutgoingAgent);
            }

            // A second, older turn from the same runner finishing late must not release the gate a
            // newer turn opened — the newer turn's push is the one the incoming agent needs.
            if (turnSessionId != null && handoff.FromTurnSessionId != null &&
                turnSessionId != handoff.FromTurnSessionId)
            {
                return new HandoffDecision(false, handoff,
                    "Confirm came from a different turn than the one that declared the handoff.",
                    HandoffConfirmCode.StaleTurn);
            }
        }

        var reason = operatorOverride
            ? JobHandoffReleaseReason.OperatorOverride
            : JobHandoffReleaseReason.OutgoingConfirmed;

        var released = await ReleaseAsync(handoff, reason, note, ct);
        return released with { Code = released.Accepted ? HandoffConfirmCode.Released : null };
    }

    public async Task<int> ExpirePendingAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = await db.JobHandoffs
            .Where(h => h.Status == JobHandoffStatus.Pending && h.ReleaseDeadlineAt <= now)
            .ToListAsync(ct);

        if (expired.Count == 0) return 0;

        var released = 0;
        foreach (var handoff in expired)
        {
            try
            {
                var decision = await ReleaseAsync(
                    handoff, JobHandoffReleaseReason.GraceExpired,
                    "Outgoing agent did not confirm within the grace window.", ct);
                if (decision.Accepted) released++;
            }
            catch (Exception ex)
            {
                // One bad handoff must not stall the sweep for the rest.
                logger?.LogError(ex, "Failed to release expired handoff {HandoffId}", handoff.Id);
                handoff.Status = JobHandoffStatus.Failed;
                handoff.Error = ex.Message;
                handoff.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }

        return released;
    }

    public async Task CompleteAsync(Guid turnSessionId, CancellationToken ct = default)
    {
        var handoff = await db.JobHandoffs
            .FirstOrDefaultAsync(h => h.ToTurnSessionId == turnSessionId
                                      && h.Status == JobHandoffStatus.InFlight, ct);
        if (handoff == null) return;

        handoff.Status = JobHandoffStatus.Completed;
        handoff.CompletedAt = DateTimeOffset.UtcNow;
        handoff.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // The incoming turn is done with the tree; let the next holder in.
        var session = await db.AgentSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == turnSessionId, ct);
        if (session?.DriverId != null)
        {
            await workspaces.ReleaseLeaseAsync(handoff.JobId, session.DriverId, ct);
        }

        await PublishHandoffAsync(handoff.JobId, handoff, null, ct);
    }

    public async Task CancelAsync(Guid jobId, string reason, CancellationToken ct = default)
    {
        var handoff = await GetOpenEntityAsync(jobId, ct);
        if (handoff == null) return;

        handoff.Status = JobHandoffStatus.Cancelled;
        handoff.Error = reason;
        handoff.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await PublishHandoffAsync(jobId, handoff, null, ct);
    }

    public async Task<JobHandoffDto?> GetDtoAsync(Guid jobId, CancellationToken ct = default)
    {
        var handoff = await GetOpenEntityAsync(jobId, ct);
        return handoff == null ? null : ToDto(handoff);
    }

    /// <summary>
    /// Opens the gate: hydrate-from-durable-state, brief the incoming persona, and dispatch.
    /// Status is flipped *before* dispatch so a crash mid-dispatch leaves the handoff released
    /// rather than pending — a duplicate release is cheaper than a permanently stuck job.
    /// </summary>
    private async Task<HandoffDecision> ReleaseAsync(
        JobHandoff handoff,
        JobHandoffReleaseReason reason,
        string? note,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        handoff.Status = JobHandoffStatus.Released;
        handoff.ReleaseReason = reason;
        handoff.HandoffNote = Truncate(note, 2000);
        handoff.ReleasedAt = now;
        handoff.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        await PublishHandoffAsync(handoff.JobId, handoff, null, ct);

        try
        {
            var job = await db.ProjectJobs.FirstOrDefaultAsync(j => j.Id == handoff.JobId, ct)
                      ?? throw new InvalidOperationException($"Job '{handoff.JobId}' vanished mid-handoff.");
            var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == handoff.ProjectId, ct)
                          ?? throw new InvalidOperationException($"Project '{handoff.ProjectId}' vanished mid-handoff.");
            var toPersona = await db.Personas.FirstOrDefaultAsync(p => p.Id == handoff.ToPersonaId, ct)
                            ?? throw new InvalidOperationException($"Persona '{handoff.ToPersonaId}' vanished mid-handoff.");

            var snapshot = await workspaces.GetSnapshotAsync(job.Id, ct);
            handoff.ExpectedHeadCommit = snapshot?.HeadCommit;

            var fromName = await DisplayNameAsync(handoff.FromPersonaId, ct);

            var briefing = $"\n[Handoff from @{fromName}]\n" +
                           $"Reason: {DescribeReason(reason)}\n" +
                           (snapshot?.HasDurableState == true
                               ? $"Durable workspace: {(string.IsNullOrWhiteSpace(snapshot.HeadCommit) ? "bundle only" : $"commit {snapshot.HeadCommit}")}" +
                                 $"{(string.IsNullOrWhiteSpace(snapshot.BundleS3Key) ? "" : $", bundle {snapshot.BundleS3Key}")}\n"
                               : "Durable workspace: none recorded yet — start from the project branch.\n") +
                           (!string.IsNullOrWhiteSpace(note) ? $"Notes from @{fromName}: {note}\n" : "");

            var lastFromPersona = await db.JobMessages.AsNoTracking()
                .Where(m => m.JobId == job.Id && m.SenderType == "persona" && m.SenderId == handoff.FromPersonaId)
                .OrderByDescending(m => m.CreatedAt)
                .FirstOrDefaultAsync(ct);

            var prompt = $"Continue this job from @{fromName}.\n\n" +
                         (lastFromPersona != null
                             ? $"[Last message from @{fromName}]:\n{lastFromPersona.Content}\n"
                             : string.Empty);

            var dispatched = await turns.DispatchAsync(new JobTurnRequest(
                job, project, toPersona, prompt, handoff.FromPersonaId, briefing,
                Attachments: null,
                Hydration: snapshot == null ? null : new JobWorkspaceHydration(
                    snapshot.GitRemoteUrl, snapshot.GitRef, snapshot.HeadCommit, snapshot.BundleS3Key)), ct);

            handoff.ToTurnSessionId = dispatched.Session.Id;
            handoff.Status = JobHandoffStatus.InFlight;
            handoff.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            await PublishHandoffAsync(job.Id, handoff, null, ct);

            logger?.LogInformation(
                "Handoff {HandoffId} released ({Reason}) on job {JobId}: @{To} dispatched as session {Session}, base {Head}",
                handoff.Id, reason, job.Id, handoff.ToPersonaId, dispatched.Session.Id, handoff.ExpectedHeadCommit);

            return new HandoffDecision(true, handoff, null);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Handoff {HandoffId} released but failed to dispatch incoming turn", handoff.Id);
            handoff.Status = JobHandoffStatus.Failed;
            handoff.Error = ex.Message;
            handoff.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            await PublishHandoffAsync(handoff.JobId, handoff, null, CancellationToken.None);
            return new HandoffDecision(false, handoff, ex.Message);
        }
    }

    private async Task<JobHandoff?> GetOpenEntityAsync(Guid jobId, CancellationToken ct)
    {
        var statuses = new[] { JobHandoffStatus.Pending, JobHandoffStatus.Released, JobHandoffStatus.InFlight };
        return await db.JobHandoffs
            .Where(h => h.JobId == jobId && statuses.Contains(h.Status))
            .OrderByDescending(h => h.DeclaredAt)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<string?> DriverOfAsync(string personaId, CancellationToken ct)
    {
        var persona = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == personaId, ct);
        return persona?.DriverId?.Trim().ToLowerInvariant();
    }

    private async Task<string> DisplayNameAsync(string personaId, CancellationToken ct)
    {
        var persona = await db.Personas.AsNoTracking().FirstOrDefaultAsync(p => p.Id == personaId, ct);
        return persona?.Name ?? personaId;
    }

    private async Task PublishHandoffAsync(Guid jobId, JobHandoff handoff, JobMessage? notice, CancellationToken ct)
    {
        if (hubContext == null) return;
        var dto = ToDto(handoff);
        await hubContext.Clients.All.SendAsync("JobHandoffUpdated", dto, ct);
        if (notice != null)
        {
            await hubContext.Clients.All.SendAsync("JobMessageAdded", new
            {
                notice.Id,
                notice.JobId,
                SenderType = notice.SenderType,
                SenderId = notice.SenderId,
                SenderName = notice.SenderName,
                notice.Content,
                CreatedAt = notice.CreatedAt
            }, ct);
        }
    }

    private static string DescribeReason(JobHandoffReleaseReason reason) => reason switch
    {
        JobHandoffReleaseReason.OutgoingConfirmed => "outgoing agent confirmed the workspace is saved",
        JobHandoffReleaseReason.GraceExpired => "grace window elapsed without confirmation; proceeding on the last durable snapshot",
        _ => "operator forced the handover"
    };

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];

    private static JobHandoffDto ToDto(JobHandoff h) => new(
        h.Id, h.JobId, h.ProjectId, h.FromPersonaId, h.ToPersonaId, h.Status,
        h.ReleaseReason?.ToString(), h.HandoffNote, h.ExpectedHeadCommit,
        h.FromTurnSessionId, h.ToTurnSessionId, h.DeclaredAt, h.ReleaseDeadlineAt,
        h.ReleasedAt, h.CompletedAt, h.Error);
}
