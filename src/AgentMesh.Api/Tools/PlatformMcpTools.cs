using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentMesh.Api.Controllers;
using AgentMesh.Api.Hubs;
using AgentMesh.Api.Services;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace AgentMesh.Api.Tools;

/// <summary>
/// The three platform-communication tools: <c>post_job_message</c>, <c>manage_task</c>, and
/// <c>open_pull_request</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why these exist.</strong> Before them, an agent that needed to ask the operator a
/// question, tick a checklist item, or hand work to a reviewer had to improvise: <c>curl</c> with
/// a hand-assembled bearer header against an internal endpoint, or a raw <c>git push</c> from a
/// container that deliberately holds no push credentials. Each attempt cost turns, and the push
/// attempt cost a turn that never came back because <c>git</c> was waiting on a credential prompt.
/// These three replace that improvisation with first-class calls whose arguments the model can
/// see on <c>tools/list</c>.
/// </para>
/// <para>
/// <strong>Context is resolved, never demanded.</strong> None of the three takes a job id as a
/// required argument, because a runner does not know its own job id — the control plane chose it.
/// <see cref="IPlatformCallContextResolver"/> recovers it from the turn session the dispatcher
/// created for this persona. An explicit <c>job_id</c> is accepted as an escape hatch for
/// out-of-band callers (the Cockpit, a script driving the mesh), and every refusal names the
/// headers that would have satisfied the lookup, so a misconfigured runner is a one-turn fix
/// rather than a mystery.
/// </para>
/// <para>
/// <strong>Failures are payloads, not exceptions.</strong> Same contract as <c>save_memory</c>:
/// the caller is a model mid-turn, and a structured <c>success:false</c> response with an
/// actionable message keeps it in the conversation. A protocol error costs it the turn and it
/// usually retries the same bad argument.
/// </para>
/// <para>
/// <strong>Wire names are snake_case and pinned by parameter name.</strong> The MCP library
/// derives a tool's published input schema from the C# parameter names, so the parameters below
/// are spelled <c>target_persona</c>, <c>requires_reply</c>, <c>task_id</c>, and
/// <c>summary_3r</c> to match the job spec exactly. Verified against ModelContextProtocol 2.2.0:
/// a parameter named <c>summary_3r</c> publishes as <c>"summary_3r"</c> and binds from that key,
/// while the same record bound as <c>summary3R</c> would publish as <c>"summary3R"</c>. The
/// <c>PlatformMcpToolsWireFormatTests</c> class pins the whole surface, because a rename on either
/// side of this boundary is silent until an agent's message silently loses its target.
/// </para>
/// </remarks>
[McpServerToolType]
public class PlatformMcpTools(
    AgentMeshDbContext db,
    IPlatformCallContextResolver contextResolver,
    IGitHubPullRequestService pullRequests,
    IHubContext<AgentMeshHub> hubContext,
    ILogger<PlatformMcpTools> logger,
    IConfiguration? configuration = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The options used to write <c>project_jobs.tasks_json</c> and <c>mentions_json</c> back to the
    /// database.
    /// </summary>
    /// <remarks>
    /// These deliberately mirror <c>ProjectJobsController.JsonOpts</c> rather than reuse
    /// <see cref="JsonOptions"/>. The blob is shared state: the controller, the Cockpit, and this
    /// tool all read and write it. Writing it indented would grow every job row for no benefit, and
    /// writing it with <c>WhenWritingNull</c> would silently drop the <c>attachments: null</c> key
    /// the controller writes, so a task round-tripped through <c>manage_task</c> would not be
    /// byte-comparable with one the dashboard touched. Same options on both sides is the invariant.
    /// </remarks>
    private static readonly JsonSerializerOptions BlobOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// The persona id that addresses the human in the loop rather than another agent.
    /// </summary>
    /// <remarks>
    /// Not a row in <c>personas</c>: the operator is whoever is reading the Cockpit. It is the one
    /// target that must resolve without a database lookup, because a fresh deployment has no
    /// persona rows at all and an agent's very first escalation is usually the one that asks a
    /// human to create them.
    /// </remarks>
    public const string OperatorPersonaId = "operator";

    /// <summary>Session status that parks a turn on the wait gate. Matches the Cockpit's vocabulary.</summary>
    public const string WaitingForInputStatus = "waiting_for_input";

    // ──────────────────────────────────────────────────────────────────────────────
    // Tool 1: post_job_message
    // ──────────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "post_job_message")]
    [Description(
        "Post an update, question, or escalation report to the shared Job Chat. Use this to escalate " +
        "blockers, ask clarifying questions, or notify reviewers. The message appears in the Cockpit in " +
        "real time. Set requires_reply=true when you are blocked and cannot proceed without an answer, " +
        "then conclude your turn — the session is parked as waiting_for_input until someone replies.")]
    public Task<string> PostJobMessage(
        [Description("The message body in Markdown. Use the 3-R format (Result / Reasoning / Risk) for escalations so the reviewer can act without opening the transcript.")]
        string content,
        [Description("Target recipient: 'operator' for the human, or a persona id such as 'astra' (tech lead), 'sorren' (architect), or 'vane' (security). The recipient must be a persona assigned to this project.")]
        string target_persona,
        [Description("Set to true if you are blocked and waiting for a response before you can proceed. Conclude your turn after calling.")]
        bool requires_reply,
        [Description("Optional job GUID. Only needed when calling outside a dispatched job turn; omit it and the job is resolved from your session.")]
        string? job_id = null,
        CancellationToken ct = default) =>
        // The wire method is an adapter and nothing more. The snake_case parameter names above are
        // what the published schema is built from; the record below is what the platform speaks.
        PostJobMessageAsync(new PostJobMessageArgs(content, target_persona, requires_reply), job_id, ct);

    /// <summary>
    /// The domain entry point behind <c>post_job_message</c>.
    /// </summary>
    /// <remarks>
    /// Split from the MCP adapter so the behaviour is callable — and testable — without a protocol
    /// layer, and so a second surface (a REST endpoint, or Job D's Temporal activity) can reuse it
    /// without inventing JSON-RPC arguments.
    /// </remarks>
    public async Task<string> PostJobMessageAsync(
        PostJobMessageArgs args,
        string? jobId = null,
        CancellationToken ct = default)
    {
        var content = args.Content;
        var target_persona = args.TargetPersona;
        var requires_reply = args.RequiresReply;

        if (string.IsNullOrWhiteSpace(content))
        {
            return Error("empty_content", "content is required and must not be blank.");
        }

        var context = await ResolveContextAsync(jobId, ct);
        if (context.Rejection is not null)
        {
            return context.Rejection;
        }

        var (job, rejection) = await LoadJobAsync(context.Context, ct);
        if (job is null)
        {
            return rejection!;
        }

        var sender = SenderIdentity.For(context.Context);
        if (sender is null)
        {
            return Error("unauthorized",
                "Could not resolve the calling agent or persona. Present a bearer token, or send the " +
                $"'{PlatformCallContextResolver.AgentIdHeader}' / '{PlatformCallContextResolver.PersonaIdHeader}' header.",
                details: new
                {
                    agentIdSource = context.Context.AgentIdSource,
                    personaIdSource = context.Context.PersonaIdSource,
                });
        }

        var target = target_persona?.Trim().TrimStart('@').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(target))
        {
            return Error("invalid_target_persona",
                $"target_persona is required. Use '{OperatorPersonaId}' for the human, or a persona id assigned to this job.");
        }

        var assignedPersonaIds = await GetAssignedPersonaIdsAsync(job.ProjectId, ct);

        // 'operator' is the human and always a valid target. Anything else must be somebody who is
        // actually on this project: posting to a persona that is not assigned produces a message no
        // one will ever be notified about, which is worse than an error — the agent believes it
        // escalated.
        if (target != OperatorPersonaId && !assignedPersonaIds.Contains(target))
        {
            return Error("unknown_target_persona",
                $"'{target}' is not a persona assigned to project '{job.ProjectId}'. " +
                (assignedPersonaIds.Count > 0
                    ? $"Assigned personas: {string.Join(", ", assignedPersonaIds)}."
                    : "This project has no personas assigned yet.") +
                $" Use '{OperatorPersonaId}' to reach the human operator.",
                details: new
                {
                    assignedPersonas = assignedPersonaIds,
                    allowedTargets = new[] { OperatorPersonaId }.Concat(assignedPersonaIds),
                });
        }

        // The @mention is what the Cockpit highlights and what a later reply-routing pass keys off.
        // Prepend it only when the agent did not already write it, so a message that begins
        // "@astra ..." is not rendered as "@astra @astra ...". The mention is added for the operator
        // too: "@operator" is how a human scanning the job sees that a message is addressed to them
        // rather than to a persona, and it costs nothing to be consistent about it.
        var body = content.Trim();
        var firstToken = body.Split([' ', '\t', '\r', '\n'], 2, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        var alreadyMentioned = firstToken.StartsWith('@') &&
            string.Equals(firstToken[1..].TrimEnd(':', ','), target, StringComparison.OrdinalIgnoreCase);
        var renderedContent = alreadyMentioned ? body : $"@{target} {body}";

        var payload = new Dictionary<string, object?>
        {
            ["requiresReply"] = requires_reply,
            ["targetPersona"] = target,
            ["senderPersona"] = sender.PersonaId,
            ["senderAgent"] = sender.AgentId,
            ["source"] = "mcp:post_job_message",
        };

        var message = new JobMessage
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            SenderType = "persona",
            SenderId = sender.PersonaId,
            SenderName = sender.DisplayName,
            Content = renderedContent,
            // The mention list is what the Cockpit parses to highlight a recipient; the operator is
            // recorded too so a reply can be routed back, even though nothing pings a human.
            MentionsJson = JsonSerializer.Serialize(new[] { target }, BlobOptions),
            TurnSessionId = context.Context.SessionId,
            StructuredPayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.JobMessages.Add(message);

        var parkedSession = requires_reply ? await ParkSessionAsync(context.Context.SessionId, ct) : null;
        job.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "post_job_message: job={JobId} from={Sender} to={Target} requiresReply={RequiresReply} message={MessageId}",
            job.Id, sender.PersonaId, target, requires_reply, message.Id);

        await hubContext.Clients.All.SendAsync("JobMessageCreated", ProjectJobsController.ToMessageDto(message), ct);

        if (parkedSession is not null)
        {
            await hubContext.Clients.All.SendAsync("SessionUpdated", parkedSession, ct);
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            messageId = message.Id,
            jobId = job.Id,
            targetPersona = target,
            requiresReply = requires_reply,
            sessionStatus = parkedSession?.Status,
            message = requires_reply
                ? $"Message posted to the Job Chat and addressed to @{target}. The session is now waiting for input; " +
                  "conclude your turn and continue when the reply arrives."
                : $"Message posted to the Job Chat and addressed to @{target}.",
            resolvedFrom = ResolveProvenance(context.Context),
        }, JsonOptions);
    }

    /// <summary>
    /// Park the calling turn on the wait gate.
    /// </summary>
    /// <remarks>
    /// Returns the tracked entity so the caller can broadcast its post-change shape, and null when
    /// there is no session to park (an out-of-band caller with no <c>X-Session-Id</c>). Deliberately
    /// does not touch <c>Agent.Status</c>: the driver is still connected and healthy, it is the
    /// <em>turn</em> that is waiting, and marking the agent offline would drop its runner from the
    /// presence list in the Cockpit.
    /// </remarks>
    private async Task<AgentSession?> ParkSessionAsync(Guid? sessionId, CancellationToken ct)
    {
        if (sessionId is null) return null;

        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return null;

        session.Status = WaitingForInputStatus;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        return session;
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Tool 2: manage_task
    // ──────────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "manage_task")]
    [Description(
        "Inspect or update checklist tasks in the current active job. Use list_all to see the sequential " +
        "checklist, get_details for one task, and set_status to move a task you own through " +
        "todo -> in_progress -> awaiting_review -> completed.")]
    public Task<string> ManageTask(
        [Description("Action to perform: 'get_details', 'set_status', or 'list_all'.")]
        string action,
        [Description("The task identifier (required for get_details and set_status). Run list_all first to obtain it.")]
        string? task_id = null,
        [Description("New status for the task (required for set_status): 'todo', 'in_progress', 'awaiting_review', or 'completed'.")]
        string? status = null,
        [Description("Optional status note or reason. Recorded on the task and shown to reviewers.")]
        string? notes = null,
        [Description("Optional job GUID. Only needed when calling outside a dispatched job turn.")]
        string? job_id = null,
        CancellationToken ct = default) =>
        ManageTaskAsync(new ManageTaskArgs(action, task_id, status, notes), job_id, ct);

    /// <summary>
    /// The domain entry point behind <c>manage_task</c>.
    /// </summary>
    public async Task<string> ManageTaskAsync(
        ManageTaskArgs args,
        string? jobId = null,
        CancellationToken ct = default)
    {
        var task_id = args.TaskId;
        var status = args.Status;
        var notes = args.Notes;

        var normalizedAction = args.Action?.Trim().ToLowerInvariant();
        if (normalizedAction is not (PlatformToolSchema.ActionGetDetails
                or PlatformToolSchema.ActionSetStatus
                or PlatformToolSchema.ActionListAll))
        {
            return Error("invalid_action",
                $"Invalid action '{args.Action}'. Allowed values: {string.Join(", ", PlatformToolSchema.AllActions.Select(a => $"'{a}'"))}.");
        }

        var context = await ResolveContextAsync(jobId, ct);
        if (context.Rejection is not null)
        {
            return context.Rejection;
        }

        var (job, rejection) = await LoadJobAsync(context.Context, ct);
        if (job is null)
        {
            return rejection!;
        }

        var tasks = ProjectJobsController.ParseTasks(job.TasksJson);
        var callerPersona = CallerPersona(context.Context);

        if (normalizedAction == PlatformToolSchema.ActionListAll)
        {
            return JsonSerializer.Serialize(new
            {
                success = true,
                jobId = job.Id,
                jobTitle = job.Title,
                jobStatus = job.Status.ToString(),
                gitBranch = job.GitBranchName,
                taskCount = tasks.Count,
                callerPersona,
                tasks = tasks.Select((t, i) => TaskView(t, i)).ToList(),
            }, JsonOptions);
        }

        var (target, notFound) = ResolveTask(tasks, task_id, callerPersona, normalizedAction);
        if (target is null)
        {
            return notFound!;
        }

        if (normalizedAction == PlatformToolSchema.ActionGetDetails)
        {
            return JsonSerializer.Serialize(new
            {
                success = true,
                jobId = job.Id,
                jobTitle = job.Title,
                task = TaskView(target, tasks.IndexOf(target)),
                assignmentRule = "A task may be moved by its assignee, or by anyone while it is unassigned.",
            }, JsonOptions);
        }

        // set_status
        if (!JobTaskStatus.TryNormalize(status, out var nextStatus))
        {
            return Error("invalid_status",
                $"Invalid status '{status}'. Allowed values: {JobTaskStatus.AllowedValuesHint()}.",
                details: new { allowedStatuses = JobTaskStatus.All });
        }

        // Order of the two remaining checks is deliberate: argument shape, then permission, then the
        // business rule. Checking the transition first would answer "cannot move todo to completed"
        // to a caller that has no right to touch the task at all — sending an agent off to insert an
        // in_progress step on somebody else's work instead of telling it the work is not its own.
        var ownershipFailure = CheckOwnership(target, callerPersona, tasks, context.Context);
        if (ownershipFailure is not null)
        {
            return ownershipFailure;
        }

        var currentStatus = target.EffectiveStatus;
        if (!JobTaskStatus.CanTransition(currentStatus, nextStatus))
        {
            return Error("invalid_transition",
                $"Cannot move task '{target.Title}' from '{currentStatus}' to '{nextStatus}'. " +
                $"Allowed next states from '{currentStatus}': {string.Join(", ", JobTaskStatus.AllowedNext(currentStatus))}. " +
                "A task must be started ('in_progress') before it can be reviewed or completed.",
                details: new
                {
                    currentStatus,
                    requestedStatus = nextStatus,
                    allowedNext = JobTaskStatus.AllowedNext(currentStatus),
                });
        }

        var previousStatus = currentStatus;
        ApplyStatus(target, nextStatus, notes);

        job.TasksJson = JsonSerializer.Serialize(tasks, BlobOptions);
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "manage_task: job={JobId} task={TaskId} '{From}' -> '{To}' by {Caller}",
            job.Id, target.Id, previousStatus, nextStatus, callerPersona ?? "(unknown)");

        await hubContext.Clients.All.SendAsync("JobTaskUpdated", new { jobId = job.Id, tasks }, ct);

        return JsonSerializer.Serialize(new
        {
            success = true,
            jobId = job.Id,
            task = TaskView(target, tasks.IndexOf(target)),
            previousStatus,
            status = nextStatus,
            message = $"Task '{target.Title}' moved from '{previousStatus}' to '{nextStatus}'.",
        }, JsonOptions);
    }

    /// <summary>
    /// Write a status onto a task and keep the pre-existing <c>IsCompleted</c> flag in agreement.
    /// </summary>
    /// <remarks>
    /// The two fields must never disagree, and <c>IsCompleted</c> is the one the React Cockpit
    /// renders today. Deriving it here is what lets the status field ship additively — old UI code
    /// keeps drawing the right checkbox while new callers get the four-state lifecycle.
    /// </remarks>
    internal static void ApplyStatus(JobTaskDto task, string status, string? notes)
    {
        task.Status = status;
        task.StatusChangedAt = DateTimeOffset.UtcNow;

        if (!string.IsNullOrWhiteSpace(notes))
        {
            task.StatusNotes = notes.Trim();
        }

        task.IsCompleted = status == JobTaskStatus.Completed;
        task.CompletedAt = status == JobTaskStatus.Completed
            ? task.CompletedAt ?? DateTimeOffset.UtcNow
            : null;
    }

    /// <summary>
    /// Resolve the task a status change or lookup targets.
    /// </summary>
    /// <remarks>
    /// <c>task_id</c> is required for both <c>get_details</c> and <c>set_status</c>, per the spec.
    /// Rather than fail with "missing task_id", an omitted id falls back to the caller's own focus
    /// task via <see cref="ProjectJobsController.ResolveTargetTask"/> — the same rule the control
    /// plane uses when it formats the checklist into the prompt. That is the task the agent was
    /// told it owns, so it is the one it means, and the response echoes which task was chosen so a
    /// wrong guess is visible instead of silent.
    /// </remarks>
    private static (JobTaskDto? Task, string? Error) ResolveTask(
        List<JobTaskDto> tasks,
        string? taskId,
        string? callerPersona,
        string action)
    {
        if (tasks.Count == 0)
        {
            return (null, Error("no_tasks", "This job has no checklist tasks. Add tasks in the Cockpit before updating their status."));
        }

        if (string.IsNullOrWhiteSpace(taskId))
        {
            var focus = ProjectJobsController.ResolveTargetTask(tasks, callerPersona);
            if (focus is null)
            {
                return (null, Error("task_id_required",
                    "task_id is required. Every task is already completed, so there is no focus task to infer; " +
                    "run list_all to see the checklist."));
            }

            return (focus, null);
        }

        if (!Guid.TryParse(taskId.Trim(), out var parsedId))
        {
            return (null, Error("invalid_task_id",
                $"task_id '{taskId}' is not a GUID. Run list_all to read the checklist with its task ids."));
        }

        var task = tasks.FirstOrDefault(t => t.Id == parsedId);
        if (task is null)
        {
            return (null, Error("task_not_found",
                $"No task '{parsedId}' in this job. Available tasks: " +
                string.Join("; ", tasks.Select((t, i) => $"{i + 1}. {t.Title} [{t.Id}] ({t.EffectiveStatus})"))));
        }

        return (task, null);
    }

    /// <summary>
    /// Enforce that a persona only moves its own work.
    /// </summary>
    /// <remarks>
    /// The invariant the job spec states ("mark <em>its assigned</em> task") and the one the
    /// dashboard already applies when it picks a focus task. An unassigned task is fair game — that
    /// is the "tell an agent to grab it" flow — and once moved it becomes the caller's, which is
    /// what stops two personas converging on the same item. A caller with no resolvable persona is
    /// refused rather than waved through, because "could not identify you" is a better answer than
    /// letting an anonymous call rewrite someone's checklist.
    /// </remarks>
    private static string? CheckOwnership(
        JobTaskDto task,
        string? callerPersona,
        List<JobTaskDto> tasks,
        PlatformCallContext context)
    {
        var assignee = task.AssignedPersonaId?.Trim().TrimStart('@').ToLowerInvariant();

        if (string.IsNullOrEmpty(assignee))
        {
            // Claiming an unassigned task is the intended way to take work.
            if (!string.IsNullOrWhiteSpace(callerPersona))
            {
                task.AssignedPersonaId = callerPersona;
            }

            return null;
        }

        if (string.IsNullOrWhiteSpace(callerPersona))
        {
            return Error("caller_identity_missing",
                $"'{task.Title}' is assigned to @{assignee} and your identity could not be resolved, so it cannot be updated. " +
                "Send a bearer token or the 'X-Persona-Id' header.",
                details: new { personaIdSource = context.PersonaIdSource, agentIdSource = context.AgentIdSource });
        }

        if (string.Equals(assignee, callerPersona, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Error("task_owned_by_other_persona",
            $"'{task.Title}' is assigned to @{assignee}, not to you (@{callerPersona}). " +
            "Reassign it in the Cockpit if the work is moving to you.",
            details: new
            {
                taskOwner = assignee,
                caller = callerPersona,
                yourTasks = tasks
                    .Where(t => string.Equals(t.AssignedPersonaId?.Trim().TrimStart('@'), callerPersona, StringComparison.OrdinalIgnoreCase))
                    .Select(t => new { t.Id, t.Title, status = t.EffectiveStatus })
                    .ToList(),
            });
    }

    private static object TaskView(JobTaskDto task, int index) => new
    {
        position = index + 1,
        id = task.Id,
        title = task.Title,
        status = task.EffectiveStatus,
        statusExplicit = task.Status is not null,
        isCompleted = task.IsCompleted,
        assignedPersonaId = task.AssignedPersonaId,
        requiresDiscussion = task.RequiresDiscussion,
        completedAt = task.CompletedAt,
        statusChangedAt = task.StatusChangedAt,
        statusNotes = task.StatusNotes,
        attachmentCount = task.Attachments?.Count ?? 0,
    };

    // ──────────────────────────────────────────────────────────────────────────────
    // Tool 3: open_pull_request
    // ──────────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "open_pull_request")]
    [Description(
        "Pushes the current working branch and opens a GitHub Pull Request against the base branch, using " +
        "credentials held by the control plane rather than by your container. Formats the PR body with the " +
        "3-R template and requests review from the project's lead. Commit your work first: this pushes " +
        "what is already committed and does not stage or commit anything.")]
    public Task<string> OpenPullRequest(
        [Description("Conventional commit title, e.g. 'feat(dashboard): add lazy paging to session list'.")]
        string title,
        [Description("The structured 3-R summary that becomes the PR body.")]
        Summary3R summary_3r,
        [Description("The local task branch to push, e.g. 'feat/task-205-session-paging'. If omitted, uses the branch recorded for this job.")]
        string? branch_name = null,
        [Description("Optional job GUID. Only needed when calling outside a dispatched job turn.")]
        string? job_id = null,
        CancellationToken ct = default) =>
        // 'summary_3r' is spelled with the underscore because the library derives the published
        // property name from the parameter name; the spec fixes that name on the wire.
        OpenPullRequestAsync(new OpenPullRequestArgs(title, summary_3r, branch_name), job_id, ct);

    /// <summary>
    /// The domain entry point behind <c>open_pull_request</c>.
    /// </summary>
    public async Task<string> OpenPullRequestAsync(
        OpenPullRequestArgs args,
        string? jobId = null,
        CancellationToken ct = default)
    {
        var title = args.Title;
        var summary_3r = args.Summary3R;
        var branch_name = args.BranchName;

        if (string.IsNullOrWhiteSpace(title))
        {
            return Error("invalid_title", "title is required. Use a conventional commit subject, e.g. 'feat(api): add tool X'.");
        }

        if (summary_3r is null || string.IsNullOrWhiteSpace(summary_3r.Result))
        {
            return Error("invalid_summary",
                "summary_3r.result is required. Describe what was built and what verifies it (files, tests).");
        }

        var context = await ResolveContextAsync(jobId, ct);
        if (context.Rejection is not null)
        {
            return context.Rejection;
        }

        var (job, rejection) = await LoadJobAsync(context.Context, ct);
        if (job is null)
        {
            return rejection!;
        }

        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == job.ProjectId, ct);

        var headBranch = FirstNonBlank(branch_name, context.Context.GitBranch, job.GitBranchName);
        if (string.IsNullOrWhiteSpace(headBranch))
        {
            return Error("branch_unresolved",
                "No branch to push: 'branch_name' was omitted and this job has no recorded branch. " +
                "Pass branch_name explicitly.",
                details: new { jobId = job.Id, jobBranch = job.GitBranchName });
        }

        var baseBranch = FirstNonBlank(context.Context.GitBaseBranch, project?.GitBaseBranch) ?? "main";

        var workspace = context.Context.WorkspaceDirectory;
        var remoteUrl = context.Context.GitRemoteUrl ?? project?.GitRepositoryUrl;

        var (owner, repo) = ResolveRepository(remoteUrl);
        var reviewer = await ResolveReviewerAsync(project, ct);

        var sender = SenderIdentity.For(context.Context);

        var result = await pullRequests.PushBranchAndCreatePrAsync(new GitHubPullRequestRequest(
            Title: title.Trim(),
            Summary: summary_3r,
            HeadBranch: headBranch.Trim(),
            BaseBranch: baseBranch,
            WorkingDirectory: workspace,
            RemoteUrl: remoteUrl,
            RepositoryOwner: owner,
            RepositoryName: repo,
            ReviewerLogin: reviewer.Login,
            SenderAgentId: sender?.PersonaId ?? sender?.AgentId,
            TeamLeadAgentId: reviewer.Login), ct);

        if (!result.Success)
        {
            logger.LogWarning(
                "open_pull_request failed for job {JobId}: {Reason} — {Message}",
                job.Id, result.FailureReason, result.Message);

            return Error(result.FailureReason ?? "pr_failed", result.Message, details: new
            {
                headBranch,
                baseBranch,
                workspaceDirectory = workspace,
                repository = owner is not null && repo is not null ? $"{owner}/{repo}" : null,
                hint = result.FailureReason == "no_working_directory"
                    ? "The control plane cannot see a checkout for this job. Save the workspace first " +
                      "(POST /api/job-workspace/{jobId}/save), or run the API and the runner on the same filesystem."
                    : null,
            });
        }

        // Linkage. The job and task rows have no PR column, and adding one means a migration; the
        // durable, already-rendered place to put it is the job ledger, which is what the Cockpit
        // reads and what a later turn re-reads. The active task also carries the note so
        // get_details answers "where is this work?".
        var activeTask = ProjectJobsController.ResolveTargetTask(
            ProjectJobsController.ParseTasks(job.TasksJson), sender?.PersonaId);

        var linkage = new Dictionary<string, object?>
        {
            ["prNumber"] = result.PrNumber,
            ["prUrl"] = result.PrUrl,
            ["headBranch"] = result.HeadBranch,
            ["baseBranch"] = result.BaseBranch,
            ["reviewer"] = reviewer.Login,
            ["simulated"] = result.Simulated,
            ["source"] = "mcp:open_pull_request",
        };

        var notification = new JobMessage
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            SenderType = "persona",
            SenderId = sender?.PersonaId ?? sender?.AgentId ?? "agentmesh",
            SenderName = sender?.DisplayName ?? "AgentMesh",
            Content = result.Message,
            MentionsJson = JsonSerializer.Serialize(
                string.IsNullOrWhiteSpace(reviewer.Login) ? Array.Empty<string>() : new[] { reviewer.Login }, BlobOptions),
            TurnSessionId = context.Context.SessionId,
            StructuredPayloadJson = JsonSerializer.Serialize(linkage, JsonOptions),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.JobMessages.Add(notification);

        if (activeTask is not null)
        {
            var tasks = ProjectJobsController.ParseTasks(job.TasksJson);
            var match = tasks.FirstOrDefault(t => t.Id == activeTask.Id);
            if (match is not null)
            {
                match.StatusNotes = result.Message;
                job.TasksJson = JsonSerializer.Serialize(tasks, BlobOptions);
            }
        }

        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await hubContext.Clients.All.SendAsync("JobMessageCreated", ProjectJobsController.ToMessageDto(notification), ct);
        if (activeTask is not null)
        {
            await hubContext.Clients.All.SendAsync("JobTaskUpdated",
                new { jobId = job.Id, tasks = ProjectJobsController.ParseTasks(job.TasksJson) }, ct);
        }

        logger.LogInformation(
            "open_pull_request: job={JobId} branch={Branch} -> {Base} as PR #{PrNumber} (simulated={Simulated})",
            job.Id, result.HeadBranch, result.BaseBranch, result.PrNumber, result.Simulated);

        return JsonSerializer.Serialize(new
        {
            success = true,
            pr_number = result.PrNumber,
            pr_url = result.PrUrl,
            status = "awaiting_review",
            simulated = result.Simulated,
            headBranch = result.HeadBranch,
            baseBranch = result.BaseBranch,
            reviewer = reviewer.Login,
            message = result.Message,
            nextStep = result.Simulated
                ? "Nothing was pushed and no review was requested (no GitHub token configured). Configure " +
                  "GitHub:Token before relying on this result."
                : "Call manage_task with action='set_status' and status='awaiting_review' when you are ready " +
                  "for the checklist item to show as in review.",
            resolvedFrom = ResolveProvenance(context.Context) with { workspace = workspace },
        }, JsonOptions);
    }

    /// <summary>
    /// The GitHub login to request review from, and where it came from.
    /// </summary>
    public sealed record ReviewerResolution(string? Login, string Source);

    /// <summary>
    /// Map the project's lead persona onto a GitHub login.
    /// </summary>
    /// <remarks>
    /// <strong>This is the one genuinely unresolved mapping in the feature, and it is an
    /// assumption stated out loud rather than a silent guess.</strong> A persona row has no GitHub
    /// login column: <see cref="Persona"/> stores an encrypted token and a git author name, neither
    /// of which is a login. Rather than add a column (a schema change that needs architect sign-off)
    /// the login is read from configuration, in this order:
    /// <list type="number">
    /// <item><description><c>GitHub:PersonaLogins:&lt;personaId&gt;</c> — the explicit map.</description></item>
    /// <item><description><c>GitHub:DefaultReviewer</c> — a single reviewer for the whole host.</description></item>
    /// <item><description>The persona id itself, on the assumption that a team naming its personas
    /// after its GitHub handles is the common case. The resolved value is echoed back in the tool
    /// response, so a wrong guess is visible in the PR rather than mysterious.</description></item>
    /// </list>
    /// If no lead can be resolved at all, the review request is skipped and the PR still opens: a
    /// project without <c>DefaultLeadPersonaId</c> set is a normal state, not a reason to fail the
    /// push the agent already paid for.
    /// </remarks>
    private async Task<ReviewerResolution> ResolveReviewerAsync(Project? project, CancellationToken ct)
    {
        var leadPersonaId = project?.DefaultLeadPersonaId;

        if (string.IsNullOrWhiteSpace(leadPersonaId))
        {
            var hostDefault = configuration?["GitHub:DefaultReviewer"];
            return string.IsNullOrWhiteSpace(hostDefault)
                ? new ReviewerResolution(null, "none configured")
                : new ReviewerResolution(hostDefault.Trim().TrimStart('@'), "config:GitHub:DefaultReviewer");
        }

        leadPersonaId = leadPersonaId.Trim().TrimStart('@').ToLowerInvariant();

        var mapped = configuration?[$"GitHub:PersonaLogins:{leadPersonaId}"];
        if (!string.IsNullOrWhiteSpace(mapped))
        {
            return new ReviewerResolution(mapped.Trim().TrimStart('@'), $"config:GitHub:PersonaLogins:{leadPersonaId}");
        }

        var displayName = await db.Personas.AsNoTracking()
            .Where(p => p.Id == leadPersonaId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync(ct);

        return new ReviewerResolution(leadPersonaId,
            displayName is null
                ? $"project lead '{leadPersonaId}' (no persona row; using the id as the login)"
                : $"project lead '{leadPersonaId}' (using the persona id as the login)");
    }

    private static (string? Owner, string? Repo) ResolveRepository(string? remoteUrl)
    {
        var parsed = GitHubPullRequestService.ParseOwnerRepo(remoteUrl);
        return parsed is null ? (null, null) : (parsed.Value.Owner, parsed.Value.Repo);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Shared plumbing
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A resolved context plus the refusal to return instead of continuing.</summary>
    private sealed record ResolvedContext(PlatformCallContext Context, string? Rejection);

    private async Task<ResolvedContext> ResolveContextAsync(string? explicitJobId, CancellationToken ct)
    {
        var context = await contextResolver.ResolveAsync(ct);

        // An explicit job_id is a fallback, not an override: if the resolver already knows the job
        // from the turn session, that is authoritative and a mismatch means the caller is confused
        // about which job it is in. Say so instead of quietly picking one.
        if (!string.IsNullOrWhiteSpace(explicitJobId))
        {
            if (!Guid.TryParse(explicitJobId.Trim(), out var parsedJobId))
            {
                return new ResolvedContext(context,
                    Error("invalid_job_id", $"job_id '{explicitJobId}' is not a GUID."));
            }

            if (context.JobId is not null && context.JobId != parsedJobId)
            {
                logger.LogWarning(
                    "open_pull_request/manage_task: caller passed job_id {Explicit} but the turn session belongs to {Actual}",
                    parsedJobId, context.JobId);
            }

            return new ResolvedContext(context with { JobId = parsedJobId, JobIdSource = "argument:job_id" }, null);
        }

        if (context.JobId is null)
        {
            return new ResolvedContext(context, Error("job_context_missing",
                "Could not resolve which job this call belongs to. Call from inside a dispatched job turn " +
                $"(the control plane then sends '{PlatformCallContextResolver.SessionIdHeader}'), send the " +
                $"'{PlatformCallContextResolver.JobIdHeader}' header, or pass job_id explicitly.",
                details: new
                {
                    sessionId = context.SessionId,
                    sessionIdSource = context.SessionIdSource,
                    jobIdSource = context.JobIdSource,
                }));
        }

        return new ResolvedContext(context, null);
    }

    private async Task<(ProjectJob? Job, string? Rejection)> LoadJobAsync(
        PlatformCallContext context,
        CancellationToken ct)
    {
        var job = await db.ProjectJobs.FirstOrDefaultAsync(j => j.Id == context.JobId!.Value, ct);
        if (job is null)
        {
            return (null, Error("job_not_found",
                $"Job '{context.JobId}' does not exist.",
                details: new { jobId = context.JobId, jobIdSource = context.JobIdSource }));
        }

        return (job, null);
    }

    private async Task<HashSet<string>> GetAssignedPersonaIdsAsync(string projectId, CancellationToken ct)
    {
        var ids = await db.ProjectPersonas
            .AsNoTracking()
            .Where(pp => pp.ProjectId == projectId)
            .Select(pp => pp.PersonaId)
            .ToListAsync(ct);

        return ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim().TrimStart('@').ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private string? CallerPersona(PlatformCallContext context) =>
        FirstNonBlank(context.PersonaId, context.AgentId);

    /// <summary>
    /// The caller's identity for a chat message: personas are who speaks in the Job Chat, agents are
    /// the machine that ran the turn.
    /// </summary>
    private sealed record SenderIdentity(string? PersonaId, string? AgentId, string DisplayName)
    {
        public static SenderIdentity? For(PlatformCallContext context)
        {
            var persona = FirstNonBlank(context.PersonaId);
            var agent = FirstNonBlank(context.AgentId);

            if (persona is null && agent is null) return null;

            return new SenderIdentity(persona, agent, persona ?? agent!);
        }
    }

    private sealed record Provenance(
        string? persona,
        string? agent,
        string? session,
        string? job,
        string? workspace = null);

    private static Provenance ResolveProvenance(PlatformCallContext context) => new(
        persona: context.PersonaIdSource,
        agent: context.AgentIdSource,
        session: context.SessionIdSource,
        job: context.JobIdSource,
        workspace: context.WorkspaceDirectory);

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string Error(string code, string message, object? details = null) =>
        JsonSerializer.Serialize(new
        {
            success = false,
            error = code,
            message,
            details,
        }, JsonOptions);
}
