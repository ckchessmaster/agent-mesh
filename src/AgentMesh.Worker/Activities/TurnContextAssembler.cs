using System.Text;
using System.Text.Json;
using AgentMesh.Core.Models;
using AgentMesh.Core.Repositories;
using AgentMesh.Core.Services;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentMesh.Worker.Activities;

/// <summary>
/// Builds the prompt stack a coding turn wakes up to.
/// </summary>
/// <remarks>
/// <para>
/// This is the pre-turn half of the job's actual purpose. The section order is fixed and each
/// section is bracketed by a literal header, because the ordering is what makes the prompt cacheable
/// and the headers are what an agent (and a test) can navigate by:
/// <list type="number">
/// <item><description><c>[PROJECT CONTEXT]</c> — project, branch, resolved working directory.</description></item>
/// <item><description><c>[PERSONA &amp; PLATFORM RULES]</c> — identity. Slowest-changing, so first.</description></item>
/// <item><description><c>[PERSISTENT MEMORY &amp; NOTES]</c> — Job B's memories.</description></item>
/// <item><description><c>[TASK ENVELOPE]</c> — title, acceptance criteria, checklist focus.</description></item>
/// <item><description><c>[WORKING CHECKPOINT]</c> — the last turn's state from
/// <c>job_workspace_states</c>.</description></item>
/// <item><description><c>[AUTOMATIC ENVIRONMENT RECOVERY NOTE]</c> — live git inspection.</description></item>
/// <item><description><c>[RECENT CONVERSATION]</c> — the sliding window.</description></item>
/// <item><description><c>[INCOMING MESSAGE]</c> — the reply or review that unblocked this turn,
/// last because recency is what a model weights most.</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Why the checkpoint and the environment note are both present.</strong> They look
/// redundant and are not. The checkpoint is what the harness recorded at the end of the last turn;
/// the note is what is true right now. After a pod restart they can disagree — the tree was left
/// dirty, someone else pushed, a stash appeared — and the disagreement is the information. Showing
/// only the note loses "what was I doing"; showing only the checkpoint re-creates the amnesia this
/// feature exists to cure, because a stale measurement presented as current is worse than none.
/// </para>
/// <para>
/// <strong>Why nothing here throws.</strong> Every read is wrapped. A memory table that is
/// unreachable, a checkpoint row that will not deserialize, or a git probe that times out each
/// degrade one section, and the turn still runs with the sections that worked. The alternative is
/// that an outage in Job B's subsystem takes task execution down with it, which is a worse failure
/// than a turn with a shorter prompt.
/// </para>
/// </remarks>
public class TurnContextAssembler(
    AgentMeshDbContext db,
    IWorkspaceStateRepository checkpoints,
    IWorkspaceReconciler reconciler,
    ILogger<TurnContextAssembler>? logger = null,
    IAgentMemoryRepository? memories = null,
    IConfiguration? config = null) : ITurnContextAssembler
{
    /// <summary>
    /// Default sliding-window size, in messages.
    /// </summary>
    /// <remarks>
    /// The spec allows 10–15; 15 is the top of that range and is the value used because the window
    /// has to cover a full exchange, not just a message count: one agent turn emits a prompt, a
    /// handful of thoughts and tool calls, a tool result, and a final response, so 15 rows is
    /// closer to three or four logical exchanges than to fifteen. Below that the agent loses the
    /// thread mid-debugging, which is where context recovery matters most.
    /// <para>
    /// Configurable because the right number depends on how chatty the driver is, and that is an
    /// operator-visible property of a deployment, not a constant we can pick correctly for everyone.
    /// </para>
    /// </remarks>
    public const int DefaultSlidingWindowSize = 15;

    /// <summary>Configuration key overriding <see cref="DefaultSlidingWindowSize"/>.</summary>
    public const string WindowSizeConfigKey = "Context:SlidingWindowSize";

    /// <summary>
    /// Hard ceiling on the window, regardless of configuration.
    /// </summary>
    /// <remarks>
    /// The window exists to keep input context lean; a mis-set config value of 10,000 would make
    /// this feature the cause of the cost it exists to prevent. The ceiling is generous enough that
    /// no sane operator hits it by accident.
    /// </remarks>
    public const int MaxSlidingWindowSize = 60;

    /// <summary>
    /// Per-message content cap inside the window.
    /// </summary>
    /// <remarks>
    /// The spec's stated concern is "excluding massive older tool dumps", and the dumps are not old
    /// — a single <c>dotnet build</c> tool result is routinely 40 KB, and it is usually one of the
    /// most recent messages, so the window alone does not exclude it. Message count bounds how many
    /// rows arrive; this bounds how big each one is. Together they are what makes the assembled
    /// prompt's size predictable, which the window alone cannot promise.
    /// <para>
    /// 4,000 characters keeps a full stack trace or a compact diff intact while cutting a whole
    /// build log. Truncation is marked in place so the agent knows the rest exists and can re-run the
    /// command deliberately rather than reasoning over a silently clipped payload.
    /// </para>
    /// </remarks>
    public const int MaxMessageContentChars = 4_000;

    /// <summary>
    /// Event types excluded from the window entirely.
    /// </summary>
    /// <remarks>
    /// <c>tool_result</c> rows are the single largest content in a session and the least useful
    /// verbatim: what the agent needs after a restart is that it ran the tests and they failed, which
    /// the accompanying <c>tool_call</c> and <c>thought</c> already say. Dropping the result payload
    /// keeps 15 messages genuinely recent rather than 15 messages that are three tool dumps deep.
    /// <para>
    /// <c>tool_call</c> is deliberately kept: it names the command, which is the only record of what
    /// was actually attempted.
    /// </para>
    /// </remarks>
    private static readonly string[] ExcludedEventTypes = ["tool_result"];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<AssembledTurnContext> AssembleAsync(
        TaskExecutionInput input,
        ChatMessageSignal? pendingReply = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var workspacePath = await ResolveWorkspacePathAsync(input, ct);

        // ── Pre-turn hook 1: reconcile the workspace ─────────────────────────────
        // Before the prompt, not after: the note has to be in the text the model reads first, or the
        // agent spends its exploration budget before it arrives.
        var report = await SafeReconcileAsync(workspacePath, ct);

        // ── Pre-turn hook 2: hydrate the checkpoint ──────────────────────────────
        var checkpoint = await SafeLoadCheckpointAsync(input, ct);

        // ── Pre-turn hook 3: persistent memories ─────────────────────────────────
        var memoryLines = await SafeLoadMemoriesAsync(input, ct);

        // ── Pre-turn hook 4: sliding window ──────────────────────────────────────
        // The loader returns newest-first because that is the shape that applies the cap. Flip it
        // here, at the boundary between the query and the renderer, so BuildPrompt renders exactly
        // the order it is handed and no reader has to remember which convention it is in.
        var (newestFirst, windowSize) = await SafeLoadWindowAsync(input, ct);
        var window = newestFirst.Reverse().ToList();

        var job = await SafeLoadJobAsync(input, ct);
        var project = await SafeLoadProjectAsync(input, ct);
        var persona = await SafeLoadPersonaAsync(input, ct);

        var prompt = BuildPrompt(
            input,
            job,
            project,
            persona,
            workspacePath,
            memoryLines,
            checkpoint,
            report,
            window,
            windowSize,
            pendingReply);

        return new AssembledTurnContext(
            Prompt: prompt,
            WorkspacePath: workspacePath,
            EnvironmentNote: EnvironmentRecoveryNote.Format(report),
            Checkpoint: checkpoint,
            HistoryMessageCount: window.Count,
            MemoryCount: memoryLines.Count);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Prompt construction
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the section stack. Internal and static so the ordering and the presence of each
    /// header are asserted without a database.
    /// </summary>
    internal static string BuildPrompt(
        TaskExecutionInput input,
        ProjectJob? job,
        Project? project,
        Persona? persona,
        string workspacePath,
        IReadOnlyList<string> memoryLines,
        TaskWorkingCheckpoint? checkpoint,
        WorkspaceReconciliationReport? report,
        IReadOnlyList<SessionMessage> window,
        int windowSize,
        ChatMessageSignal? pendingReply)
    {
        var sb = new StringBuilder();

        // 1. Project context. The resolved directory is stated explicitly and named as authoritative,
        // because "don't run pwd" only works if the agent already knows the answer.
        sb.Append("[PROJECT CONTEXT]\n");
        sb.Append("Project: ").Append(project?.Name ?? input.ProjectId).Append(" (").Append(input.ProjectId).Append(")\n");
        sb.Append("Repository: ").Append(project?.RepositoryName ?? "(unknown)").Append('\n');
        sb.Append("Working Directory: ").Append(workspacePath)
          .Append(" — this is authoritative; do not run pwd or ls to confirm it.\n");
        sb.Append("Target Branch: ").Append(input.TargetBranch).Append('\n');
        if (!string.IsNullOrWhiteSpace(project?.ProjectInstructions))
        {
            sb.Append("Project Guidelines: ").Append(project!.ProjectInstructions.Trim()).Append('\n');
        }

        if (job is not null && !string.IsNullOrWhiteSpace(job.GitBranchName) &&
            !string.Equals(job.GitBranchName, input.TargetBranch, StringComparison.Ordinal))
        {
            sb.Append("Job Branch: ").Append(job.GitBranchName).Append('\n');
        }
        sb.Append('\n');

        // 2. Identity.
        sb.Append("[PERSONA & PLATFORM RULES]\n");
        if (persona is not null)
        {
            sb.Append("You are ").Append(persona.Name);
            if (!string.IsNullOrWhiteSpace(persona.Role)) sb.Append(" — ").Append(persona.Role);
            sb.Append(".\n");
            if (!string.IsNullOrWhiteSpace(persona.PersonaPrompt))
            {
                sb.Append(persona.PersonaPrompt.Trim()).Append('\n');
            }
        }
        else
        {
            sb.Append("Persona id: ").Append(input.PersonaId ?? "(none)").Append('\n');
        }
        sb.Append("Work the task in the working directory above. Use the platform tools " +
                  "(post_job_message, manage_task, open_pull_request) rather than describing " +
                  "progress in prose.\n\n");

        // 3. Persistent memories (Job B).
        if (memoryLines.Count > 0)
        {
            sb.Append("[PERSISTENT MEMORY & NOTES]\n");
            sb.Append("Lessons you saved earlier. These are still true unless you disprove them; " +
                      "do not re-derive them.\n");
            foreach (var line in memoryLines)
            {
                sb.Append("* ").Append(line).Append('\n');
            }
            sb.Append('\n');
        }

        // 4. Task envelope.
        sb.Append("[TASK ENVELOPE]\n");
        var focus = ResolveFocusTask(job, input.TaskId);
        sb.Append("Task: ").Append(focus?.Title ?? job?.Title ?? input.TaskId).Append('\n');
        if (job is not null && !string.IsNullOrWhiteSpace(job.Description))
        {
            sb.Append("Job: ").Append(job.Title.Trim()).Append('\n');
            sb.Append("Objective: ").Append(job.Description.Trim()).Append('\n');
        }
        if (focus is not null)
        {
            sb.Append("Checklist item status: ").Append(focus.EffectiveStatus).Append('\n');
        }
        AppendChecklist(sb, job, input.TaskId);
        sb.Append('\n');

        // 5. Working checkpoint from the previous turn.
        if (checkpoint is { IsEmpty: false })
        {
            sb.Append("[WORKING CHECKPOINT]\n");
            sb.Append("Recorded by the harness at the end of your last turn on this task " +
                      $"({checkpoint.UpdatedAt.ToUniversalTime():yyyy-MM-dd HH:mm 'UTC'}):\n");
            if (!string.IsNullOrWhiteSpace(checkpoint.ActiveBranch))
                sb.Append("* Branch then: ").Append(checkpoint.ActiveBranch).Append('\n');
            if (!string.IsNullOrWhiteSpace(checkpoint.HeadCommit))
                sb.Append("* Head then: ").Append(checkpoint.HeadCommit).Append('\n');
            // FilesOrEmpty, not ModifiedFiles.Count: a checkpoint whose file column is NULL ("the tree
            // was never inspected") reconstructs with a null list, and dereferencing it would throw
            // inside prompt assembly — turning a stale-checkpoint edge case into a failed turn.
            if (checkpoint.FilesOrEmpty.Count > 0)
            {
                sb.Append("* Uncommitted then: ").Append(checkpoint.FilesOrEmpty.Count)
                  .Append(" file(s) — the environment note below reports what is uncommitted now.\n");
            }
            if (!string.IsNullOrWhiteSpace(checkpoint.ActiveStash))
                sb.Append("* Stash then: ").Append(checkpoint.ActiveStash).Append('\n');
            if (!string.IsNullOrWhiteSpace(checkpoint.LastSummary))
            {
                sb.Append("* Last summary: ").Append(CollapseWhitespace(checkpoint.LastSummary)).Append('\n');
            }
            sb.Append('\n');
        }

        // 6. Environment recovery note (live).
        var environmentNote = EnvironmentRecoveryNote.Format(report);
        if (!string.IsNullOrWhiteSpace(environmentNote))
        {
            sb.Append(environmentNote).Append('\n');
            if (ShouldWarnAboutDrift(checkpoint, report))
            {
                sb.Append("\nThe checkpoint above and the note below were taken at different times; " +
                          "where they disagree, the note is current.\n");
            }
            sb.Append('\n');
        }

        // 7. Sliding window.
        sb.Append("[RECENT CONVERSATION]\n");
        if (window.Count == 0)
        {
            sb.Append("(no prior messages in this session — this is the first turn)\n\n");
        }
        else
        {
            sb.Append("Last ").Append(window.Count).Append(" message(s) of this session");
            if (window.Count >= windowSize)
            {
                sb.Append(" (window capped at ").Append(windowSize).Append("; older context lives in the checkpoint and memories above)");
            }
            sb.Append(":\n");

            // Rendered in the order handed in, which AssembleAsync guarantees is chronological (the
            // loader fetches newest-first only to apply the cap). A reversed transcript is the kind
            // of defect that surfaces as "the agent keeps re-answering its own last question" and
            // never as an error, so the ordering is asserted in the tests rather than trusted.
            foreach (var message in window)
            {
                sb.Append("- [").Append(message.CreatedAt.ToUniversalTime().ToString("HH:mm"))
                  .Append(' ').Append(message.EventType).Append("] ")
                  .Append(FormatWindowMessage(message)).Append('\n');
            }
            sb.Append('\n');
        }

        // 8. The message that unblocked this turn, last and untruncated.
        if (pendingReply is not null)
        {
            sb.Append("[INCOMING MESSAGE]\n");
            sb.Append("From: ").Append(string.IsNullOrWhiteSpace(pendingReply.SenderId) ? "operator" : pendingReply.SenderId)
              .Append(" at ")
              .Append(pendingReply.Timestamp.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture))
              .Append(" UTC")
              .Append('\n');
            sb.Append(pendingReply.Content.Trim()).Append('\n');
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// True when the checkpoint and the live probe disagree about branch or HEAD.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Compares the two structured reports rather than parsing the rendered note. The difference is
    /// not cosmetic: a text match against <c>environmentNote.Contains(shortHead)</c> reports "no
    /// drift" whenever the commit hash happens to appear anywhere in the note — including in a
    /// filename in the dirty-file list — which is exactly the false negative that hides the case the
    /// warning exists for.
    /// </para>
    /// <para>
    /// The warning is only printed on an actual mismatch. Printing it always would train the agent to
    /// ignore it, and the case where it matters — a restart during which someone else pushed, or the
    /// tree changed under the last turn — is exactly where the agent must stop and look rather than
    /// continue as if nothing moved.
    /// </para>
    /// <para>
    /// Only branch and HEAD are compared. The dirty-file count is deliberately excluded: a turn that
    /// edits files and then checkpoints will always differ from the previous turn's count, so
    /// including it would make the warning fire on every ordinary turn and defeat its purpose.
    /// </para>
    /// </remarks>
    internal static bool ShouldWarnAboutDrift(
        TaskWorkingCheckpoint? checkpoint,
        WorkspaceReconciliationReport? report)
    {
        if (checkpoint is null || checkpoint.IsEmpty) return false;
        if (report is null || !report.Inspected || !report.IsGitRepository) return false;

        // Branch drift. Skipped when either side is unknown: "unknown" is the absence of a
        // measurement, not a measurement of a different value, and comparing against it would warn on
        // every probe that could not read the branch.
        if (!string.IsNullOrWhiteSpace(checkpoint.ActiveBranch) &&
            !report.DetachedHead &&
            !string.IsNullOrWhiteSpace(report.Branch) &&
            !string.Equals(checkpoint.ActiveBranch.Trim(), report.Branch.Trim(), StringComparison.Ordinal))
        {
            return true;
        }

        // Head drift. The checkpoint stores `git log -1 --oneline` output ("a1b2c3d message"), the
        // probe stores the same shape, so the comparison is on the leading token — the hash — which is
        // the only part that identifies the commit. Matching the whole line would warn on every turn
        // because the message column carries a differing timestamp-free but still distinct rendering
        // once anything is amended.
        if (!string.IsNullOrWhiteSpace(checkpoint.HeadCommit) &&
            !string.IsNullOrWhiteSpace(report.HeadCommitOneline))
        {
            var then = HeadToken(checkpoint.HeadCommit);
            var now = HeadToken(report.HeadCommitOneline);
            if (then.Length > 0 && now.Length > 0 &&
                !then.StartsWith(now, StringComparison.OrdinalIgnoreCase) &&
                !now.StartsWith(then, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The leading whitespace-delimited token of an <c>--oneline</c> string.</summary>
    private static string HeadToken(string value)
    {
        var trimmed = value.Trim();
        var space = trimmed.IndexOf(' ');
        return space < 0 ? trimmed : trimmed[..space];
    }

    /// <summary>
    /// Renders one window row, capped and with the tool payload reduced to what it names.
    /// </summary>
    internal static string FormatWindowMessage(SessionMessage message)
    {
        var content = message.Content ?? string.Empty;

        // A tool_call row is often an empty content plus a JSON argument blob. Without this the most
        // informative part of the row — which command was run — is missing from the window.
        if (string.IsNullOrWhiteSpace(content) &&
            string.Equals(message.EventType, "tool_call", StringComparison.OrdinalIgnoreCase))
        {
            content = DescribeToolCall(message);
        }

        content = CollapseWhitespace(content);

        if (content.Length <= MaxMessageContentChars) return content;

        // The marker is written so the agent can tell where the harness stopped and can go look for
        // the rest deliberately, rather than concluding the payload ended mid-sentence.
        return content.Substring(0, MaxMessageContentChars) +
               $" …[truncated by harness: {content.Length - MaxMessageContentChars} chars omitted]";
    }

    private static string DescribeToolCall(SessionMessage message)
    {
        var name = string.IsNullOrWhiteSpace(message.ToolName) ? "tool" : message.ToolName;

        if (string.IsNullOrWhiteSpace(message.ToolArgsJson)) return $"{name} (no arguments recorded)";

        try
        {
            using var doc = JsonDocument.Parse(message.ToolArgsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return $"{name} {CollapseWhitespace(message.ToolArgsJson)}";
            }

            // `command` is the argument that carries the intent for the two tools an agent uses most
            // (run_command and a shell-driven git call). Naming it beats listing every argument,
            // which is what makes a window row readable at a glance.
            foreach (var key in new[] { "command", "path", "query", "url", "prompt" })
            {
                if (doc.RootElement.TryGetProperty(key, out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    return $"{name}: {CollapseWhitespace(value.GetString()!)}";
                }
            }

            return $"{name} {CollapseWhitespace(message.ToolArgsJson)}";
        }
        catch (JsonException)
        {
            return $"{name} (arguments not parseable)";
        }
    }

    /// <summary>
    /// Collapses runs of whitespace to single spaces, keeping the window row to one line.
    /// </summary>
    /// <remarks>
    /// One line per message is what makes the window scannable, and it also removes the failure where
    /// a pasted 200-line log inside a message consumes the whole section. Newlines inside a quoted
    /// value are replaced rather than dropped so the text stays readable.
    /// </remarks>
    internal static string CollapseWhitespace(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.IndexOfAny(['\n', '\r', '\t']) < 0 && !value.Contains("  ", StringComparison.Ordinal))
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var c in value)
        {
            var isSpace = char.IsWhiteSpace(c);
            if (isSpace)
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }

        return sb.ToString().Trim();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Section loaders — each one degrades independently
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The directory this task runs in: the workflow input when it names one, otherwise the project's
    /// resolved path.
    /// </summary>
    /// <remarks>
    /// The input wins because it is the value the workflow was started with and every turn in one run
    /// must agree on it; re-resolving from the project row would let an edit to <c>projects</c>
    /// mid-flight move a running task's working directory between its own turns. The project row is
    /// the fallback for a workflow started before the path was known, and for a row whose stored path
    /// was recorded against an older repository name.
    /// </remarks>
    internal async Task<string> ResolveWorkspacePathAsync(TaskExecutionInput input, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(input.WorkspacePath))
        {
            return WorkspacePathResolver.Normalize(input.WorkspacePath.Trim());
        }

        try
        {
            var project = await db.Projects
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == input.ProjectId, ct);

            if (project is not null)
            {
                return project.GetResolvedWorkingDirectory();
            }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            logger?.LogWarning(ex, "Could not resolve the working directory for project {ProjectId}", input.ProjectId);
        }

        // Last resort. WorkspacePathResolver.ReposRoot rather than "/workspace": the latter is the
        // sandbox's own fallback and would put commands in the same directory as every other project,
        // which is the exact multi-repo bug this feature removes. A path under ReposRoot at least
        // fails as "directory does not exist" — visible, and it does not corrupt another repo's tree.
        return WorkspacePathResolver.ReposRoot;
    }

    private async Task<WorkspaceReconciliationReport?> SafeReconcileAsync(string workspacePath, CancellationToken ct)
    {
        try
        {
            var report = await reconciler.ReconcileAsync(workspacePath, ct);
            if (!report.Inspected)
            {
                logger?.LogInformation(
                    "Workspace reconciliation produced no note for {Workspace}: {Reason}",
                    workspacePath, report.FailureReason ?? "(unspecified)");
            }

            return report;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Workspace reconciliation failed for {Workspace}", workspacePath);
            return null;
        }
    }

    private async Task<TaskWorkingCheckpoint?> SafeLoadCheckpointAsync(TaskExecutionInput input, CancellationToken ct)
    {
        try
        {
            return await checkpoints.GetCheckpointAsync(input.JobId, input.TaskId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not load the checkpoint for job {JobId} task {TaskId}", input.JobId, input.TaskId);
            return null;
        }
    }

    private async Task<IReadOnlyList<string>> SafeLoadMemoriesAsync(TaskExecutionInput input, CancellationToken ct)
    {
        if (memories is null) return [];

        try
        {
            // Agent first, persona second. Memories are written under whichever identity the caller
            // resolved (MemoryContextResolver prefers the authenticated claim, then X-Agent-Id), and a
            // driver row and a persona row can both legitimately own lessons for the same project.
            // Reading both is what makes the section useful after a persona change on the same task.
            var agentMemories = await memories.GetMemoriesForAgentAsync(input.AgentId, input.ProjectId, ct);
            var personaMemories = string.IsNullOrWhiteSpace(input.PersonaId) ||
                                  string.Equals(input.PersonaId, input.AgentId, StringComparison.OrdinalIgnoreCase)
                ? []
                : await memories.GetMemoriesForAgentAsync(input.PersonaId!, input.ProjectId, ct);

            var lines = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var memory in agentMemories.Concat(personaMemories))
            {
                var value = CollapseWhitespace(memory.Value);
                if (value.Length == 0 || !seen.Add($"{memory.Key}:{value}")) continue;

                // Category is free text on the model (AgentMemory.Category has no enum), and an
                // uncategorised lesson is common — save_memory accepts a category but does not require
                // one. Rendering a blank one as "[] key: value" puts an empty bracket where a reader
                // expects a label, so the tag is omitted entirely when there is nothing to say.
                var tag = string.IsNullOrWhiteSpace(memory.Category)
                    ? string.Empty
                    : $"[{memory.Category.Trim()}] ";

                lines.Add($"{tag}{memory.Key}: {value}");
            }

            return CapMemories(lines);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not load memories for agent {AgentId} project {ProjectId}", input.AgentId, input.ProjectId);
            return [];
        }
    }

    /// <summary>
    /// Caps the rendered memory list.
    /// </summary>
    /// <remarks>
    /// <see cref="IAgentMemoryRepository.GetMemoriesForAgentAsync"/> is unbounded — Job B's contract
    /// deliberately returns every matching row. An agent that saves lessons diligently will
    /// eventually have more of them than the prompt can carry, and the memory section would then
    /// crowd out the environment note and the sliding window, which are the sections that stop this
    /// turn from re-running <c>pwd</c>. The repository is ordered by category then recency, so
    /// keeping the head of the list keeps the most recently refreshed lessons in each category.
    /// <para>
    /// The count of what was dropped is reported rather than the omission being silent: an agent that
    /// knows it has unread notes will ask for them, and an agent that believes it saw everything will
    /// not.
    /// </para>
    /// </remarks>
    internal const int MaxRenderedMemories = 40;

    internal static IReadOnlyList<string> CapMemories(IReadOnlyList<string> lines)
    {
        if (lines.Count <= MaxRenderedMemories) return lines;

        var kept = lines.Take(MaxRenderedMemories).ToList();
        kept.Add($"… {lines.Count - MaxRenderedMemories} more saved memories not shown " +
                 "(call the memory listing tool to read them)");
        return kept;
    }

    /// <summary>
    /// Loads the sliding window: the newest N messages, newest-first, plus the size that was applied.
    /// </summary>
    /// <remarks>
    /// Returned newest-first on purpose. The caller renders oldest-first (that is what reads
    /// correctly), but the test that proves the cap works asserts on the newest message's presence and
    /// the oldest one's absence, which is unambiguous in either order.
    /// <para>
    /// The filter is applied <em>before</em> <c>Take</c>, not after. Filtering after the cap would
    /// mean "the last 15 rows, minus whichever ones turn out to be tool results", so a session whose
    /// recent rows are tool-heavy would yield a window of three messages when fifteen were asked for
    /// — and the agent would lose the thread precisely in the busy stretches where it needs it.
    /// </para>
    /// </remarks>
    internal async Task<(IReadOnlyList<SessionMessage> Window, int WindowSize)> SafeLoadWindowAsync(
        TaskExecutionInput input,
        CancellationToken ct)
    {
        var windowSize = ResolveWindowSize();

        var sessionId = await ResolveTurnSessionIdAsync(input, ct);
        if (sessionId is null)
        {
            return ([], windowSize);
        }

        try
        {
            var rows = await db.SessionMessages
                .AsNoTracking()
                .Where(m => m.SessionId == sessionId
                            && !ExcludedEventTypes.Contains(m.EventType))
                .OrderByDescending(m => m.CreatedAt)
                .ThenByDescending(m => m.Id)
                .Take(windowSize)
                .ToListAsync(ct);

            return (rows, windowSize);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not load the conversation window for session {SessionId}", sessionId);
            return ([], windowSize);
        }
    }

    /// <summary>
    /// The turn session whose conversation this task is having.
    /// </summary>
    /// <remarks>
    /// Resolution order, most specific first:
    /// <list type="number">
    /// <item><description>The session recorded on the checkpoint — the one that actually produced the
    /// last turn of <em>this task</em>.</description></item>
    /// <item><description>The session on the workflow input, when the starter knew it.</description></item>
    /// <item><description>The newest session whose metadata names this job, scoped to the persona when
    /// the input carries one.</description></item>
    /// </list>
    /// <para>
    /// Step 3 is a metadata lookup because <c>agent_sessions</c> has no job id column: the dispatcher
    /// writes <c>jobId</c> into <c>metadata_json</c> and that is the only link that exists. It is
    /// filtered in memory rather than translated to SQL — <c>metadata_json</c> is jsonb and the
    /// in-memory provider the tests run on cannot translate a json path, so a SQL-side filter would
    /// make this the untested path. Sessions are read newest-first and the scan stops at the first
    /// match, so the cost is bounded by how many sessions are loaded, not by the table size.
    /// </para>
    /// </remarks>
    internal async Task<Guid?> ResolveTurnSessionIdAsync(TaskExecutionInput input, CancellationToken ct)
    {
        if (Guid.TryParse(input.TurnSessionId?.Trim(), out var fromInput))
        {
            return fromInput;
        }

        try
        {
            var checkpoint = await checkpoints.GetCheckpointAsync(input.JobId, input.TaskId, ct);
            if (Guid.TryParse(checkpoint?.TurnSessionId?.Trim(), out var fromCheckpoint))
            {
                return fromCheckpoint;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Checkpoint lookup for the turn session failed on job {JobId}", input.JobId);
        }

        if (!Guid.TryParse(input.JobId.Trim(), out var jobId)) return null;

        try
        {
            // Newest first, bounded: a job with a long history does not need every session row read
            // to find the current one, and an unbounded read here would grow with the job forever.
            var candidates = await db.AgentSessions
                .AsNoTracking()
                .Where(s => s.UpdatedAt >= DateTimeOffset.UtcNow.AddDays(-30))
                .OrderByDescending(s => s.UpdatedAt)
                .Take(200)
                .ToListAsync(ct);

            foreach (var session in candidates)
            {
                if (!MetadataMatchesJob(session.MetadataJson, jobId)) continue;
                if (!string.IsNullOrWhiteSpace(input.PersonaId) &&
                    !string.Equals(session.PersonaId, input.PersonaId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return session.Id;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Session lookup by job {JobId} failed", input.JobId);
        }

        return null;
    }

    /// <summary>
    /// True when a session's metadata names this job.
    /// </summary>
    /// <remarks>
    /// Tolerant of malformed JSON for the same reason <c>MemoryContextResolver</c> is: metadata is
    /// written by several call sites, and a blob that will not parse must degrade to "not this job"
    /// rather than abort the whole window load.
    /// </remarks>
    internal static bool MetadataMatchesJob(string? metadataJson, Guid jobId)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return false;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("jobId", out var value))
            {
                return false;
            }

            var raw = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            return Guid.TryParse(raw?.Trim(), out var parsed) && parsed == jobId;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private int ResolveWindowSize()
    {
        var configured = config?.GetValue<int?>(WindowSizeConfigKey);
        if (configured is null or <= 0) return DefaultSlidingWindowSize;

        return Math.Min(configured.Value, MaxSlidingWindowSize);
    }

    private async Task<ProjectJob?> SafeLoadJobAsync(TaskExecutionInput input, CancellationToken ct)
    {
        if (!Guid.TryParse(input.JobId.Trim(), out var jobId)) return null;

        try
        {
            return await db.ProjectJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            logger?.LogWarning(ex, "Could not load job {JobId}", input.JobId);
            return null;
        }
    }

    private async Task<Project?> SafeLoadProjectAsync(TaskExecutionInput input, CancellationToken ct)
    {
        try
        {
            return await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == input.ProjectId, ct);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            logger?.LogWarning(ex, "Could not load project {ProjectId}", input.ProjectId);
            return null;
        }
    }

    private async Task<Persona?> SafeLoadPersonaAsync(TaskExecutionInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.PersonaId)) return null;

        try
        {
            return await db.Personas.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == input.PersonaId, ct);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            logger?.LogWarning(ex, "Could not load persona {PersonaId}", input.PersonaId);
            return null;
        }
    }

    /// <summary>
    /// The checklist item this workflow owns, or null when the job has no matching task.
    /// </summary>
    internal static JobTaskDto? ResolveFocusTask(ProjectJob? job, string taskId)
    {
        if (job is null || string.IsNullOrWhiteSpace(taskId)) return null;

        var tasks = ParseTasks(job.TasksJson);
        if (tasks.Count == 0) return null;

        if (Guid.TryParse(taskId.Trim(), out var parsed))
        {
            var match = tasks.FirstOrDefault(t => t.Id == parsed);
            if (match is not null) return match;
        }

        // No match by id. Returning null rather than "the first incomplete task" is deliberate: this
        // workflow was started for a specific task, and labelling a different task as its focus would
        // put the wrong acceptance criteria in front of an agent that is working on the right one.
        return null;
    }

    private static void AppendChecklist(StringBuilder sb, ProjectJob? job, string taskId)
    {
        if (job is null) return;

        var tasks = ParseTasks(job.TasksJson);
        if (tasks.Count == 0) return;

        sb.Append("Checklist (").Append(tasks.Count(t => t.IsCompleted)).Append('/').Append(tasks.Count)
          .Append(" complete):\n");

        for (var i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];
            var isFocus = Guid.TryParse(taskId.Trim(), out var parsed) && task.Id == parsed;

            sb.Append("  ").Append(task.IsCompleted ? "[x]" : "[ ]")
              .Append(' ').Append(i + 1).Append(". ").Append(task.Title);

            if (!string.IsNullOrWhiteSpace(task.AssignedPersonaId))
            {
                sb.Append(" (@").Append(task.AssignedPersonaId).Append(')');
            }

            if (isFocus) sb.Append("  ← this workflow");
            sb.Append('\n');
        }
    }

    private static List<JobTaskDto> ParseTasks(string? tasksJson)
    {
        if (string.IsNullOrWhiteSpace(tasksJson)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<JobTaskDto>>(tasksJson, JsonOpts) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// True for failures that mean "this section is unavailable" rather than "this code is wrong".
    /// </summary>
    /// <remarks>
    /// Narrow on purpose. The tempting version is <c>catch (Exception)</c> everywhere, and it turns a
    /// real defect — a mis-mapped entity, an NRE in the renderer — into a silently short prompt that
    /// the agent works around, which is a bug that never surfaces. These three are the failures a
    /// healthy process produces against an unhealthy database: a save/read conflict, a provider that
    /// cannot translate a query, and a missing row. Cancellation is excluded and re-thrown by every
    /// caller that needs to distinguish it.
    /// </remarks>
    private static bool IsRecoverable(Exception ex) =>
        ex is not OperationCanceledException &&
        ex is DbUpdateException or InvalidOperationException or KeyNotFoundException;
}
