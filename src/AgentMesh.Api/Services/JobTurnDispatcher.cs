using AgentMesh.Api.Hubs;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AgentMesh.Api.Services;

public record JobTurnRequest(
    ProjectJob Job,
    Project Project,
    Persona Persona,
    string Prompt,
    /// <summary>Display name of whoever the prompt is on behalf of (persona id or "operator").</summary>
    string Sender,
    /// <summary>Extra lines appended to the prompt, e.g. handoff briefing. Must end with a newline.</summary>
    string? PreflightContext = null,
    List<JobAttachmentDto>? Attachments = null,
    /// <summary>Snapshot the incoming runner should hydrate from. Null = hydrate from whatever is durable.</summary>
    JobWorkspaceHydration? Hydration = null);

public record JobWorkspaceHydration(
    string? GitRemoteUrl,
    string? GitRef,
    string? HeadCommit,
    string? BundleS3Key);

public record DispatchedJobTurn(AgentSession Session, SessionMessage PromptMessage, Agent Driver);

/// <summary>
/// Single place that turns "persona X should work on job Y" into a turn session + prompt +
/// runner broadcast.
///
/// This existed as three near-identical copies (job chat, queued-turn drain, autonomous handoff).
/// They had already drifted — only two of them attached files, and none of them could agree on
/// where the work should live. Per-job workspaces are only safe if every dispatch path points the
/// runner at the same directory, so the paths have to be one path.
/// </summary>
public class JobTurnDispatcher(
    AgentMeshDbContext db,
    IHubContext<AgentMeshHub>? hubContext = null,
    IJobWorkspaceService? workspaces = null)
{
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<DispatchedJobTurn> DispatchAsync(
        JobTurnRequest req,
        CancellationToken ct = default)
    {
        var job = req.Job;
        var project = req.Project;
        var persona = req.Persona;

        var driver = await ResolveDriverAsync(persona, ct);
        var driverId = driver.Id;

        var turnSessionId = Guid.NewGuid();

        var attachments = req.Attachments ?? new List<JobAttachmentDto>();
        if (attachments.Count > 0)
        {
            StageAttachments(job.Id, turnSessionId, attachments);
        }

        // The workspace pointer is the handoff contract: it tells the incoming runner which commit
        // it is expected to be standing on. Without it, two machines can each "finish" the job
        // against different baselines and nobody notices until a push is rejected.
        var hydration = req.Hydration;
        if (hydration == null && workspaces != null)
        {
            var state = await workspaces.GetAsync(job.Id, ct);
            if (state != null)
            {
                hydration = new JobWorkspaceHydration(
                    state.GitRemoteUrl ?? project.GitRepositoryUrl,
                    state.GitRef ?? job.GitBranchName,
                    state.HeadCommit,
                    state.BundleS3Key);
            }
        }

        var turnMetadata = new Dictionary<string, object?>
        {
            ["jobId"] = job.Id.ToString(),
            ["projectId"] = project.Id,
            ["projectName"] = project.Name,
            ["workspaceType"] = project.WorkspaceType.ToString(),
            ["gitBranch"] = job.GitBranchName,
            ["gitUrl"] = project.GitRepositoryUrl,
            ["gitBaseBranch"] = project.GitBaseBranch,
            ["s3ArchiveKey"] = job.S3ArchiveKey,
            ["projectInstructions"] = project.ProjectInstructions,
            ["personaId"] = persona.Id,
            ["personaName"] = persona.Name,
            ["personaRole"] = persona.Role,
            ["personaPrompt"] = persona.PersonaPrompt,
            ["workingDirectory"] = persona.DefaultWorkingDirectory,
            ["model"] = persona.DefaultModel,
            ["effort"] = persona.DefaultEffort,
            ["mode"] = persona.DefaultMode,
            ["tier"] = persona.DefaultTier,
            ["contextSize"] = persona.DefaultContextSize,
            ["attachments"] = attachments,
            // Per-job workspace: the runner keys its checkout off jobId, not sessionId, so every
            // persona on every machine converges on one durable tree for this job.
            ["workspaceScope"] = "job",
            ["workspaceGitRemote"] = hydration?.GitRemoteUrl ?? project.GitRepositoryUrl,
            ["workspaceGitRef"] = hydration?.GitRef ?? job.GitBranchName,
            ["workspaceHeadCommit"] = hydration?.HeadCommit,
            ["workspaceBundleKey"] = hydration?.BundleS3Key
        };

        var turnSession = new AgentSession
        {
            Id = turnSessionId,
            Title = $"Job Turn: {job.Title} (@{persona.Name})",
            Status = "thinking",
            MetadataJson = JsonSerializer.Serialize(turnMetadata, JsonOpts),
            PersonaId = persona.Id,
            DriverId = driverId,
            CreatorAgentId = driverId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.AgentSessions.Add(turnSession);

        var promptWithContext =
            $"[Project Context: Project \"{project.Name}\" ({project.WorkspaceType}), Branch: \"{job.GitBranchName}\"]\n" +
            (!string.IsNullOrWhiteSpace(project.ProjectInstructions)
                ? $"[Project Guidelines: {project.ProjectInstructions}]\n"
                : "") +
            $"[Job Context: {job.Title} - {job.Description}]\n" +
            (req.PreflightContext ?? string.Empty) +
            "\n" +
            req.Prompt;

        var promptMsg = new SessionMessage
        {
            SessionId = turnSession.Id,
            SenderAgentId = req.Sender,
            TargetAgentId = driverId,
            EventType = "prompt",
            Content = promptWithContext,
            StructuredPayloadJson = attachments.Count > 0
                ? JsonSerializer.Serialize(new { attachments }, JsonOpts)
                : null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SessionMessages.Add(promptMsg);

        if (AgentMeshHub.IsAgentConnected(driverId))
        {
            driver.Status = "busy";
            driver.LastHeartbeatAt = DateTimeOffset.UtcNow;
        }

        if (workspaces != null)
        {
            await workspaces.AcquireLeaseAsync(job.Id, driverId, persona.Id, ct);
        }

        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await BroadcastAsync(turnSession, promptMsg, driver, persona, job, project, promptWithContext, attachments, hydration, ct);

        return new DispatchedJobTurn(turnSession, promptMsg, driver);
    }

    private async Task BroadcastAsync(
        AgentSession session,
        SessionMessage promptMsg,
        Agent driver,
        Persona persona,
        ProjectJob job,
        Project project,
        string prompt,
        List<JobAttachmentDto> attachments,
        JobWorkspaceHydration? hydration,
        CancellationToken ct)
    {
        if (hubContext == null) return;

        var driverId = driver.Id;
        var payload = new
        {
            sessionId = session.Id,
            messageId = promptMsg.Id,
            agentId = driverId,
            driverId = driverId,
            personaId = persona.Id,
            personaName = persona.Name,
            personaRole = persona.Role,
            personaPrompt = persona.PersonaPrompt,
            prompt,
            sender = promptMsg.SenderAgentId,
            workingDirectory = persona.DefaultWorkingDirectory,
            model = persona.DefaultModel,
            effort = persona.DefaultEffort,
            mode = persona.DefaultMode,
            tier = persona.DefaultTier,
            contextSize = persona.DefaultContextSize,
            jobId = job.Id,
            projectId = project.Id,
            gitBranch = job.GitBranchName,
            createdAt = promptMsg.CreatedAt,
            attachments,
            workspaceScope = "job",
            workspaceGitRemote = hydration?.GitRemoteUrl ?? project.GitRepositoryUrl,
            workspaceGitRef = hydration?.GitRef ?? job.GitBranchName,
            workspaceHeadCommit = hydration?.HeadCommit,
            workspaceBundleKey = hydration?.BundleS3Key
        };

        var group = hubContext.Clients.Group($"agent-{driverId}");
        if (group != null)
        {
            await group.SendAsync("PromptReceived", payload, ct);
        }
        await hubContext.Clients.All.SendAsync("PromptReceived", payload, ct);
    }

    /// <summary>
    /// Personas reference a driver by id; the driver row may not exist yet (a persona can be
    /// authored before its runner has ever checked in). Creating it on demand keeps dispatch from
    /// hard-failing on a typo'd or not-yet-launched driver.
    /// </summary>
    private async Task<Agent> ResolveDriverAsync(Persona persona, CancellationToken ct)
    {
        var driverId = persona.DriverId.Trim().ToLowerInvariant();
        var driver = await db.Agents.FirstOrDefaultAsync(a => a.Id == driverId, ct);
        if (driver == null)
        {
            driver = new Agent
            {
                Id = driverId,
                Name = $"Driver {driverId}",
                RuntimeType = RuntimeType.CliAgy,
                TrustTier = TrustTier.SeniorManager
            };
            db.Agents.Add(driver);
        }
        return driver;
    }

    private static void StageAttachments(Guid jobId, Guid turnSessionId, List<JobAttachmentDto> attachments)
    {
        var sessionAttachDir = Controllers.SessionsController.GetAttachmentDirectory(turnSessionId);
        var jobAttachDir = Controllers.ProjectJobsController.GetAttachmentDirectory(jobId);
        Directory.CreateDirectory(sessionAttachDir);

        foreach (var att in attachments)
        {
            var safeName = Controllers.SessionsController.SanitizeFileName(att.FileName);
            var src = Path.Combine(jobAttachDir, $"{att.Id}_{safeName}");
            if (!File.Exists(src))
            {
                src = Directory.GetFiles(jobAttachDir, $"{att.Id}_*").FirstOrDefault()
                      ?? Directory.GetFiles(jobAttachDir, $"*{att.Id}*").FirstOrDefault();
            }
            if (!File.Exists(src)) continue;

            try
            {
                File.Copy(src, Path.Combine(sessionAttachDir, $"{att.Id}_{safeName}"), overwrite: true);
            }
            catch
            {
                // Attachment staging is best-effort: a missing file should not block the turn.
            }
        }
    }
}
