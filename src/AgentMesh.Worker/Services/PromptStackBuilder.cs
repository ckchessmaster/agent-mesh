using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentMesh.Core.Models;
using AgentMesh.Core.Repositories;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentMesh.Worker.Services;

/// <summary>
/// The platform's single prompt composer: builds a turn's context into the layered,
/// volatility-ordered stack described by <see cref="IPromptStackBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the order is the contract.</strong> An inference engine's prefix cache is keyed on the
/// leading bytes of a prompt. Put a per-turn value — a branch name, a dirty-file count — anywhere in
/// the system block and every byte after it misses on every turn, which is the cost this class exists
/// to remove. So the layers are emitted strictly from least to most volatile, and a layer that would
/// change mid-session is not allowed in the cached block at all. The split is physical, not
/// conventional: layers 1–5 are returned in
/// <see cref="AssembledPromptStack.CachedSystemPrompt"/> and layer 6 in
/// <see cref="AssembledPromptStack.UserTaskEnvelope"/>, so a caller cannot accidentally concatenate
/// them into one block without doing it explicitly.
/// </para>
/// <para>
/// <strong>Why this replaced <c>TurnContextAssembler</c>.</strong> Job D shipped eight flat sections
/// covering roughly the same ground in a different order, with persona and project instructions sitting
/// after the branch name — cache-uncorrect — and no team topology at all. Two composers means two
/// answers to "what does an agent wake up knowing", and the divergence between them is invisible until
/// an agent behaves differently on two paths. The sections that carried weight (checkpoint, live git
/// probe, sliding window, per-message caps) were absorbed into the layers that own them; the ordering
/// was corrected.
/// </para>
/// <para>
/// <strong>Why nothing here throws.</strong> Every read is wrapped. A memory table that is
/// unreachable, a checkpoint row that will not deserialize, or a git probe that times out each degrade
/// one layer, and the turn still runs with the layers that worked. The alternative is that an outage in
/// one subsystem takes task execution down with it, which is a worse failure than a turn with a
/// shorter prompt.
/// </para>
/// <para>
/// <strong>Why the environment note is in the user turn, not the system block.</strong> It reports
/// what is true <em>right now</em> — dirty count, stash, HEAD — and therefore changes between two
/// turns of the same task. In the system block it invalidates the cache for every layer below it,
/// which is all of them. In the envelope it costs exactly what it changes.
/// </para>
/// </remarks>
public class PromptStackBuilder(
    AgentMeshDbContext db,
    IWorkspaceStateRepository checkpoints,
    IWorkspaceReconciler reconciler,
    ILogger<PromptStackBuilder>? logger = null,
    IAgentMemoryRepository? memories = null,
    IConfiguration? config = null) : IPromptStackBuilder
{
    /// <summary>
    /// Default sliding-window size, in messages.
    /// </summary>
    /// <remarks>
    /// The spec allows 10–15; 15 is the top of that range and is the value used because the window has
    /// to cover a full exchange, not just a message count: one agent turn emits a prompt, a handful of
    /// thoughts and tool calls, a tool result, and a final response, so 15 rows is closer to three or
    /// four logical exchanges than to fifteen. Below that the agent loses the thread mid-debugging,
    /// which is where context recovery matters most.
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
    /// The window exists to keep input context lean; a mis-set config value of 10,000 would make this
    /// feature the cause of the cost it exists to prevent. The ceiling is generous enough that no sane
    /// operator hits it by accident.
    /// </remarks>
    public const int MaxSlidingWindowSize = 60;

    /// <summary>
    /// Per-message content cap inside the window.
    /// </summary>
    /// <remarks>
    /// The spec's stated concern is "excluding massive older tool dumps", and the dumps are not old — a
    /// single <c>dotnet build</c> tool result is routinely 40 KB, and it is usually one of the most
    /// recent messages, so the window alone does not exclude it. Message count bounds how many rows
    /// arrive; this bounds how big each one is. Together they are what makes the assembled prompt's
    /// size predictable, which the window alone cannot promise.
    /// <para>
    /// 4,000 characters keeps a full stack trace or a compact diff intact while cutting a whole build
    /// log. Truncation is marked in place so the agent knows the rest exists and can re-run the command
    /// deliberately rather than reasoning over a silently clipped payload.
    /// </para>
    /// </remarks>
    public const int MaxMessageContentChars = 4_000;

    /// <summary>
    /// The markdown rule separating cached layers.
    /// </summary>
    /// <remarks>
    /// A literal <c>---</c> rather than a blank line because it has to be (a) findable by a test
    /// asserting layer count and ordering, (b) a boundary a model recognises as a section break, and
    /// (c) byte-stable. Anything carrying a timestamp or a counter would defeat the cache the divider
    /// exists to protect.
    /// </remarks>
    public const string LayerDivider = "\n\n---\n\n";

    /// <summary>
    /// Event types excluded from the window entirely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>tool_result</c> rows are the single largest content in a session and the least useful
    /// verbatim: what the agent needs after a restart is that it ran the tests and they failed, which
    /// the accompanying <c>tool_call</c> and <c>thought</c> already say. Dropping the result payload
    /// keeps 15 messages genuinely recent rather than 15 messages that are three tool dumps deep.
    /// </para>
    /// <para>
    /// <c>tool_call</c> is deliberately kept: it names the command, which is the only record of what
    /// was actually attempted.
    /// </para>
    /// <para>
    /// Excluding <c>tool_result</c> is also what keeps the window legal to replay as a chat payload. A
    /// <c>role: "tool"</c> message is only valid when it answers a preceding assistant
    /// <c>tool_calls</c> entry by id, and the harness stores no <c>tool_call_id</c> on a session row, so
    /// a replayed tool result would be an orphan that OpenAI-compatible servers reject with a 400. The
    /// information survives as the <c>tool_call</c> row's text; the protocol violation does not.
    /// </para>
    /// <para>
    /// <c>artifact</c> and <c>system</c> rows are harness bookkeeping, not conversation: an artifact row
    /// carries a blob reference and a system row carries a status notice, and neither is something the
    /// model said or should be replayed to it as if it were.
    /// </para>
    /// </remarks>
    private static readonly string[] ExcludedEventTypes = ["tool_result", "artifact", "system"];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <inheritdoc />
    public async Task<AssembledPromptStack> BuildPromptStackAsync(
        PromptStackContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var workspacePath = await ResolveWorkspacePathAsync(context, ct);

        // The reads run sequentially rather than in parallel on purpose. They share one
        // AgentMeshDbContext, which is not thread-safe and is scoped per activity; issuing concurrent
        // queries through it throws "A second operation was started on this context", which would make
        // the degraded-layer guarantee above fire on every turn under load.
        var project = await SafeLoadProjectAsync(context, ct);
        var persona = await SafeLoadPersonaAsync(context, ct);
        var job = await SafeLoadJobAsync(context, ct);
        var roster = await SafeLoadRosterAsync(context, ct);
        var memoryLines = await SafeLoadMemoriesAsync(context, ct);
        var checkpoint = await SafeLoadCheckpointAsync(context, ct);

        // Pre-turn, not post-turn: the note has to be in the text the model reads first, or the agent
        // spends its exploration budget rediscovering `git status` before it arrives.
        var report = await SafeReconcileAsync(workspacePath, ct);

        var (newestFirst, windowSize) = await SafeLoadWindowAsync(context, checkpoint, ct);
        var window = newestFirst.Reverse().ToList();

        var cachedSystemPrompt = BuildCachedSystemPrompt(
            context,
            project,
            persona,
            roster,
            memoryLines,
            workspacePath);

        var envelope = BuildTaskEnvelope(
            context,
            project,
            job,
            workspacePath,
            checkpoint,
            report,
            window,
            windowSize);

        return new AssembledPromptStack(
            CachedSystemPrompt: cachedSystemPrompt,
            UserTaskEnvelope: envelope,
            History: MapHistory(window),
            WorkspacePath: workspacePath,
            EnvironmentNote: EnvironmentRecoveryNote.Format(report),
            Checkpoint: checkpoint,
            HistoryMessageCount: window.Count,
            MemoryCount: memoryLines.Count);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Layers 1–5: the cached prefix
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders layers 1 through 5 in strict stability order.
    /// </summary>
    /// <remarks>
    /// Internal and near-static so ordering, dividers and content are asserted without a database.
    /// Every value interpolated here must be one that changes no more often than the layer it sits in:
    /// a per-turn value in this method is the exact regression the feature exists to prevent, which is
    /// why the branch name, the task title and the git state are all in the envelope instead.
    /// </remarks>
    internal static string BuildCachedSystemPrompt(
        PromptStackContext context,
        Project? project,
        Persona? persona,
        IReadOnlyList<RosterMember> roster,
        IReadOnlyList<string> memoryLines,
        string workspacePath)
    {
        var layers = new List<string>(5)
        {
            PlatformPrompt(),
            PersonaPrompt(persona, context),
            ProjectPrompt(project, workspacePath),
            TeamPrompt(roster, context),
            MemoryPrompt(memoryLines, context),
        };

        return string.Join(LayerDivider, layers.Select(l => l.TrimEnd())) + "\n";
    }

    /// <summary>
    /// Layer 1 — platform invariants. 100% static; cacheable across every agent and project.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The numbers are read from the code that enforces them rather than typed in, because a prompt
    /// that states a stale invariant is worse than one that states none: an agent told the command
    /// ceiling is 180s will plan around 180s after an operator raises it, and will then misread its own
    /// timeout as a bug in its command. Same for the spillover path.
    /// </para>
    /// <para>
    /// Deliberately contains no ids, paths, branches or counts. Anything project- or job-specific here
    /// would break the cross-project cache this layer is the sole beneficiary of.
    /// </para>
    /// </remarks>
    internal static string PlatformPrompt()
    {
        var sb = new StringBuilder();
        sb.Append("[LAYER 1: PLATFORM INVARIANTS]\n");
        sb.Append(Prompts.PlatformPrompts.PlatformInvariants);
        return sb.ToString();
    }

    /// <summary>
    /// Layer 2 — master persona prompt. Static per agent identity (<c>agents.persona_prompt</c>).
    /// </summary>
    /// <remarks>
    /// The persona's own text is emitted verbatim and unadorned wherever it is usable, because it is
    /// authored by a human as the agent's identity and re-summarising it here would both cost tokens
    /// and silently edit someone's persona. The fallbacks exist for the case where the row is missing
    /// or blank — a persona created before its prompt was written — where an agent with no identity at
    /// all is the worse outcome.
    /// </remarks>
    internal static string PersonaPrompt(Persona? persona, PromptStackContext context)
    {
        var sb = new StringBuilder();
        sb.Append("[LAYER 2: MASTER PERSONA]\n");

        if (persona is null)
        {
            sb.Append("Persona: ").Append(context.PersonaId ?? context.AgentId).Append('\n');
            sb.Append("No persona row was found for this identity, so no role boundaries or trust tier " +
                      "are on file. Act as a conservative default: implement the task, escalate " +
                      "anything destructive or out of scope, and ask before assuming product intent.\n");
            return sb.ToString();
        }

        sb.Append("You are ").Append(persona.Name);
        if (!string.IsNullOrWhiteSpace(persona.Role)) sb.Append(" — ").Append(persona.Role);
        sb.Append(".\n");
        sb.Append("Persona id: ").Append(persona.Id).Append('\n');
        sb.Append("Trust tier: ").Append(persona.TrustTier).Append('\n');
        if (!string.IsNullOrWhiteSpace(persona.GitAuthorName) || !string.IsNullOrWhiteSpace(persona.GitAuthorEmail))
        {
            sb.Append("Commit author: ")
              .Append(string.IsNullOrWhiteSpace(persona.GitAuthorName) ? "(name unset)" : persona.GitAuthorName.Trim())
              .Append(" <")
              .Append(string.IsNullOrWhiteSpace(persona.GitAuthorEmail) ? "(email unset)" : persona.GitAuthorEmail.Trim())
              .Append(">\n");
        }

        if (!string.IsNullOrWhiteSpace(persona.PersonaPrompt))
        {
            sb.Append('\n').Append(persona.PersonaPrompt.Trim()).Append('\n');
        }
        else
        {
            sb.Append("\nThe persona's own prompt is empty on this deployment. Default to the 3-R " +
                      "communication standard: lead with the Result (bottom line plus file paths and " +
                      "line numbers), then the Reasoning (why this approach over the alternatives), " +
                      "then the Risk / Next Step (what could break, who reviews).\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Layer 3 — project manifest. Static per repository
    /// (<c>projects.instructions_markdown</c> / <c>.agentmesh.json</c>).
    /// </summary>
    /// <remarks>
    /// The resolved working directory lives here rather than in the envelope even though the spec lists
    /// it under both, and the reason is the cache: <c>/workspace/repos/{repo_name}</c> is a property of
    /// the repository, so it changes exactly as often as this layer does — once per project. Putting it
    /// in the cached block costs nothing and lets the envelope's git block stay about git. The envelope
    /// still names the directory, because the envelope is the part that survives context compaction and
    /// has to be readable on its own.
    /// </remarks>
    internal static string ProjectPrompt(Project? project, string workspacePath)
    {
        var sb = new StringBuilder();
        sb.Append("[LAYER 3: PROJECT MANIFEST]\n");

        if (project is null) {
            sb.Append("(no project row; conventions unavailable)\n");
            return sb.ToString();
        }

        sb.Append("Project: ").Append(project.Name).Append(" (").Append(project.Id).Append(")\n");
        if (!string.IsNullOrWhiteSpace(project.Description))
        {
            sb.Append("Summary: ").Append(CollapseWhitespace(project.Description)).Append('\n');
        }
        sb.Append("Workspace type: ").Append(project.WorkspaceType).Append('\n');
        sb.Append("Repository name: ").Append(project.RepositoryName).Append('\n');
        sb.Append("Working root: ").Append(workspacePath)
          .Append(" — authoritative; do not run pwd or ls to confirm it.\n");
        if (!string.IsNullOrWhiteSpace(project.GitRepositoryUrl))
        {
            sb.Append("Git remote: ").Append(project.GitRepositoryUrl.Trim()).Append('\n');
        }
        sb.Append("Base branch: ").Append(project.GitBaseBranch).Append('\n');

        if (!string.IsNullOrWhiteSpace(project.ProjectInstructions))
        {
            sb.Append("\n[PROJECT CONVENTIONS & BUILD CONTRACTS]\n");
            sb.Append(project.ProjectInstructions.Trim()).Append('\n');
        }
        else
        {
            sb.Append("\nNo project instructions are recorded. Discover the contract from the " +
                      "repository itself (README, AGENTS.md, CI workflow, solution/task files) and " +
                      "save what you learn with save_memory so the next turn does not re-derive it.\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Layer 4 — team topology and escalation paths. Semi-dynamic; set per job.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rendered from the project's actual roster (<c>project_personas</c>) rather than a hardcoded
    /// cast list. A prompt that names "Astra, Tech Lead" when Astra left three months ago sends
    /// escalations into a void, and the agent reports "escalated" while nobody was notified — the
    /// confidently-wrong failure this whole feature is aimed at.
    /// </para>
    /// <para>
    /// The self-exclusion matters: an agent told to escalate to the Tech Lead while it <em>is</em> the
    /// Tech Lead posts its escalation to its own queue and waits for a reply from itself. That is a
    /// deadlock that looks like an unresponsive teammate.
    /// </para>
    /// </remarks>
    internal static string TeamPrompt(IReadOnlyList<RosterMember> roster, PromptStackContext context)
    {
        var sb = new StringBuilder();
        sb.Append("[LAYER 4: TEAM TOPOLOGY & ESCALATION PATHS]\n");

        if (roster.Count == 0)
        {
            sb.Append("No roster is assigned to this project. Escalate to the operator in Job Chat " +
                      "and address the message to them by name; an unaddressed message may not resume " +
                      "anyone.\n");
            return sb.ToString();
        }

        sb.Append("[TEAM ROSTER & ESCALATION PATHS]\n");

        // Sorted here as well as in the query. The loader orders by name, but the cache property belongs
        // to the renderer: if a future caller builds a roster from a second source, or the provider
        // changes its plan, an unordered render reshuffles the block and every byte from Layer 4 down
        // misses on a schedule nobody can reproduce. Role-then-name is the order that reads as an
        // org chart rather than a phone list.
        foreach (var member in roster.OrderBy(m => m.Role, StringComparer.Ordinal)
                                     .ThenBy(m => m.Name, StringComparer.Ordinal))
        {
            sb.Append("* ").Append(member.Role).Append(": ").Append(member.Name)
              .Append(" (id: ").Append(member.PersonaId).Append(")");

            if (member.IsSelf)
            {
                sb.Append(" — this is you; never escalate to yourself");
            }
            else if (!string.IsNullOrWhiteSpace(member.EscalationFor))
            {
                sb.Append(" — escalate ").Append(member.EscalationFor).Append(" to them");
            }

            if (!string.IsNullOrWhiteSpace(member.TrustTier))
            {
                sb.Append(" [tier: ").Append(member.TrustTier).Append(']');
            }
            sb.Append('\n');
        }

        sb.Append("\nEscalate rather than guess: schema migrations, breaking changes to public " +
                  "contracts, and ordering or durability guarantees go to the architect; recurring " +
                  "test failures, test-runner deadlocks and code review go to the tech lead; auth " +
                  "boundaries and vulnerability findings go to the security lead; missing product " +
                  "requirements and scope trade-offs go to the operator.\n");
        sb.Append("Post escalations in Job Chat with the 3-R shape and yield your turn — do not keep " +
                  "working past the boundary you just escalated.\n");

        return sb.ToString();
    }

    /// <summary>
    /// Layer 5 — persistent agent memories. Low volatility; scoped to (agent_id, project_id).
    /// </summary>
    internal static string MemoryPrompt(IReadOnlyList<string> memoryLines, PromptStackContext context)
    {
        var sb = new StringBuilder();
        sb.Append("[LAYER 5: PERSISTENT MEMORY & NOTES (project: ").Append(context.ProjectId).Append(")]\n");

        if (memoryLines.Count == 0)
        {
            sb.Append("No memories saved for this agent and project yet. When you verify a quirk — a " +
                      "required test flag, a mocking requirement, a path that is not what it looks " +
                      "like — save it with save_memory so the next turn starts knowing it.\n");
            return sb.ToString();
        }

        sb.Append("Lessons you saved earlier. These are still true unless you disprove them; do not " +
                  "re-derive them.\n");
        foreach (var line in memoryLines)
        {
            sb.Append("* ").Append(line).Append('\n');
        }

        return sb.ToString();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Layer 6: the turn-0 task envelope
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders layer 6: the task manifest, the working environment, and the git recovery header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This block is dispatched as the primary <c>user</c> message. It is never appended as a trailing
    /// system message, which is the second failure this job fixes: an instruction-tuned model that
    /// reads a system message at the tail concludes environment setup has <em>just</em> completed and
    /// that the user has not yet asked anything — so it reverts to "I will wait for the user's
    /// question" and the turn burns itself doing nothing.
    /// </para>
    /// <para>
    /// The block is therefore written to be self-sufficient as a user turn: it names the objective
    /// imperatively, states the status, and closes with the next action. A reader with no other context
    /// must be able to tell what it is supposed to be doing.
    /// </para>
    /// <para>
    /// The checkpoint and the live note are both present and are not redundant. The checkpoint is what
    /// the harness recorded at the end of the last turn; the note is what is true right now. After a
    /// pod restart they can disagree — the tree was left dirty, someone else pushed, a stash appeared —
    /// and the disagreement is the information. Showing only the note loses "what was I doing"; showing
    /// only the checkpoint re-creates the amnesia this exists to cure, because a stale measurement
    /// presented as current is worse than none.
    /// </para>
    /// </remarks>
    internal static string BuildTaskEnvelope(
        PromptStackContext context,
        Project? project,
        ProjectJob? job,
        string workspacePath,
        TaskWorkingCheckpoint? checkpoint,
        WorkspaceReconciliationReport? report,
        IReadOnlyList<SessionMessage> window,
        int windowSize)
    {
        var sb = new StringBuilder();
        var focus = ResolveFocusTask(job, context.TaskId);

        sb.Append("[TASK ENVELOPE & WORKING ENVIRONMENT]\n");
        sb.Append("* Target Project: ").Append(project?.Name ?? context.ProjectId)
          .Append(" (").Append(context.ProjectId).Append(")\n");
        sb.Append("* Working Root: ").Append(workspacePath)
          .Append("  — authoritative; do not run pwd or ls to confirm it.\n");
        sb.Append("* Active Branch: ").Append(ResolveBranch(context, job)).Append('\n');
        sb.Append("* Task ID: #").Append(context.TaskId);
        if (focus is not null) sb.Append(" (\"").Append(focus.Title.Trim()).Append("\")");
        sb.Append('\n');

        if (focus is not null && !string.IsNullOrWhiteSpace(focus.AcceptanceCriteria))
        {
            sb.Append("* Acceptance Criteria: ").Append(CollapseWhitespace(focus.AcceptanceCriteria)).Append('\n');
        }
        sb.Append("* Status: ").Append(focus?.EffectiveStatus ?? job?.Status.ToString() ?? "unknown").Append('\n');

        if (job is not null && !string.IsNullOrWhiteSpace(job.Description))
        {
            sb.Append("* Job: ").Append(job.Title.Trim()).Append(" — ").Append(CollapseWhitespace(job.Description)).Append('\n');
        }

        if (context.TaskId == "stage-1-discovery")
        {
            sb.Append("\n[STAGE 1: DISCOVERY & DECOMPOSITION]\n");
            sb.Append("* Phase: Read-only Discovery. Inspect the codebase (grep, find, read_file, git log).\n");
            sb.Append("* Objective: Decompose the job into 3 to 6 atomic tasks and register them with manage_task(set_checklist).\n");
            sb.Append("* Post plan & questions via post_job_message(requires_reply: true), then conclude your turn. Do not begin implementation.\n");
        }

        AppendChecklist(sb, job, context.TaskId);

        // The live probe, not the checkpoint: this is the section that stops a resumed agent from
        // spending its first five calls on `git status`.
        var environmentNote = EnvironmentRecoveryNote.Format(report);
        if (!string.IsNullOrWhiteSpace(environmentNote))
        {
            sb.Append('\n').Append(environmentNote).Append('\n');
            if (ShouldWarnAboutDrift(checkpoint, report))
            {
                sb.Append("\nThe checkpoint below and the note above were taken at different times; " +
                          "where they disagree, the note is current.\n");
            }
        }

        if (checkpoint is { IsEmpty: false })
        {
            sb.Append("\n[WORKING CHECKPOINT — end of your previous turn]\n");
            sb.Append("Recorded by the harness at ")
              .Append(checkpoint.UpdatedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture))
              .Append(":\n");
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
                  .Append(" file(s) — the note above reports what is uncommitted now.\n");
            }
            if (!string.IsNullOrWhiteSpace(checkpoint.ActiveStash))
                sb.Append("* Stash then: ").Append(checkpoint.ActiveStash).Append('\n');
            if (!string.IsNullOrWhiteSpace(checkpoint.LastSummary))
            {
                sb.Append("* Last summary: ").Append(CollapseWhitespace(checkpoint.LastSummary)).Append('\n');
            }
        }

        // The window is NOT rendered as text here. It travels as structured
        // AssembledPromptStack.History messages, which the runner emits as real turns.
        //
        // Job D rendered it inline because its output was one flat string with nowhere else to put it.
        // With the split, inline rendering would mean the same conversation reaches the model twice —
        // once as messages between the prefix and the envelope, once again inside the envelope's own
        // text — which doubles the cost of the section and gives the model two slightly different
        // renderings of the same turns to reconcile.
        //
        // What stays is a one-line statement of whether this turn is a continuation. That is the part
        // the structured messages cannot carry: an agent that sees no history section cannot tell
        // "nothing happened before" from "the harness dropped it", and the second reading is the one
        // that makes it re-run work it already did.
        sb.Append("\n[CONVERSATION STATE]\n");
        if (window.Count > 0)
        {
            sb.Append("This turn continues the session: ").Append(window.Count).Append(" prior message(s)")
              .Append(window.Count >= windowSize ? $" (window capped at {windowSize}" : string.Empty);

            if (window.Count >= windowSize)
            {
                sb.Append("; older context lives in the checkpoint and memories");
            }
            sb.Append(window.Count >= windowSize ? ")." : ".")
              .Append(" They are supplied as their own turns immediately before this one, oldest " +
                      "first — read them before repeating any command they show already ran.\n");
        }
        else
        {
            sb.Append("No prior messages in this session: this is the first turn of the task. Nothing " +
                      "has been attempted yet, so nothing here needs to be re-verified before you " +
                      "start.\n");
        }

        // The reply that unblocked this turn, last and untruncated: recency is what a model weights
        // most, and a review comment buried mid-prompt is a review comment that gets ignored.
        if (context.PendingReply is { } reply)
        {
            sb.Append("\n[INCOMING MESSAGE]\n");
            sb.Append("From: ").Append(string.IsNullOrWhiteSpace(reply.SenderId) ? "operator" : reply.SenderId)
              .Append(" at ")
              .Append(reply.Timestamp.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
              .Append(" UTC\n");
            sb.Append(reply.Content.Trim()).Append('\n');
        }

        sb.Append("\n[OBJECTIVE]\n");
        sb.Append(ResolveObjective(context, focus));

        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// The imperative next action, which is what makes this a user turn rather than a status report.
    /// </summary>
    /// <remarks>
    /// A blank objective is the common case for a macro turn: the workflow input names a task, not a
    /// sentence. Falling back to the focus item's title, then to the job title, then to an explicit
    /// "pick the next incomplete item" keeps the block actionable at every level of missing data. The
    /// last resort matters most — an envelope whose objective is empty is exactly the payload that
    /// produces the idle-loop reply this job exists to eliminate.
    /// </remarks>
    internal static string ResolveObjective(PromptStackContext context, JobTaskDto? focus)
    {
        if (!string.IsNullOrWhiteSpace(context.UserPrompt))
        {
            return context.UserPrompt.Trim();
        }

        if (focus is not null && !string.IsNullOrWhiteSpace(focus.Title))
        {
            return $"Work the checklist item above to completion: {focus.Title.Trim()}\n" +
                   "Report with the 3-R shape, update the item's status with manage_task, and open a " +
                   "PR for review when the scoped tests pass. Do not reply that you are waiting for " +
                   "instructions — this envelope is the instruction.";
        }

        return "Continue the task named above. Report with the 3-R shape, update the checklist status " +
               "with manage_task, and open a PR for review when the scoped tests pass. Do not reply " +
               "that you are waiting for instructions — this envelope is the instruction.";
    }

    /// <summary>The branch to state: the job's own branch when it has one, else the input's.</summary>
    private static string ResolveBranch(PromptStackContext context, ProjectJob? job) =>
        !string.IsNullOrWhiteSpace(job?.GitBranchName) ? job!.GitBranchName.Trim() : context.TargetBranch;

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

        sb.Append("\nChecklist (").Append(tasks.Count(t => t.IsCompleted)).Append('/').Append(tasks.Count)
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

    // ──────────────────────────────────────────────────────────────────────────────
    // Sliding window
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps window rows onto wire roles, oldest first, capped and de-duplicated.
    /// </summary>
    /// <remarks>
    /// <c>prompt</c> rows become <c>user</c> and everything the agent produced becomes
    /// <c>assistant</c>, which is the shape a chat-completions payload expects. The mapping is
    /// deliberately lossy about <c>tool_call</c> arguments: the harness has no stored
    /// <c>tool_call_id</c>, so a replayed assistant tool call would demand a matching tool result the
    /// platform cannot supply, and OpenAI-compatible servers reject that pairing with a 400. Rendering
    /// the call as assistant text keeps the information ("I ran the tests and they failed") without
    /// claiming a protocol pairing the row cannot honour.
    /// </remarks>
    internal static IReadOnlyList<TurnHistoryMessage> MapHistory(IReadOnlyList<SessionMessage> window)
    {
        var mapped = new List<TurnHistoryMessage>(window.Count);
        foreach (var message in window)
        {
            var content = FormatWindowMessage(message);
            if (content.Length == 0) continue;

            mapped.Add(new TurnHistoryMessage(MapRole(message.EventType), content, message.EventType));
        }

        return mapped;
    }

    /// <summary>The wire role a session event maps to.</summary>
    internal static string MapRole(string? eventType) => eventType?.ToLowerInvariant() switch
    {
        "prompt" => "user",
        "thought" or "tool_call" or "response" or "assistant" => "assistant",
        "tool_result" => "tool",
        _ => "user",
    };

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
        return content[..MaxMessageContentChars] +
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
            // (run_command and a shell-driven git call). Naming it beats listing every argument, which
            // is what makes a window row readable at a glance.
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
    /// One line per message is what makes the window scannable, and it also removes the failure where a
    /// pasted 200-line log inside a message consumes the whole section. Newlines inside a quoted value
    /// are replaced rather than dropped so the text stays readable.
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

    /// <summary>
    /// True when the checkpoint and the live probe disagree about branch or HEAD.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Compares the two structured reports rather than parsing the rendered note. The difference is not
    /// cosmetic: a text match against <c>environmentNote.Contains(shortHead)</c> reports "no drift"
    /// whenever the commit hash happens to appear anywhere in the note — including in a filename in the
    /// dirty-file list — which is exactly the false negative that hides the case the warning exists for.
    /// </para>
    /// <para>
    /// The warning is only printed on an actual mismatch. Printing it always would train the agent to
    /// ignore it, and the case where it matters — a restart during which someone else pushed, or the
    /// tree changed under the last turn — is exactly where the agent must stop and look rather than
    /// continue as if nothing moved.
    /// </para>
    /// <para>
    /// Only branch and HEAD are compared. The dirty-file count is deliberately excluded: a turn that
    /// edits files and then checkpoints will always differ from the previous turn's count, so including
    /// it would make the warning fire on every ordinary turn and defeat its purpose.
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

        // Head drift. The checkpoint stores `git log -1 --oneline` output ("a1b2c3d message"), the probe
        // stores the same shape, so the comparison is on the leading token — the hash — which is the
        // only part that identifies the commit. Matching the whole line would warn on every turn because
        // the message column carries a differing timestamp-free but still distinct rendering once
        // anything is amended.
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

    // ──────────────────────────────────────────────────────────────────────────────
    // Section loaders — each one degrades independently
    // ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The directory this task runs in: the caller's path when it names one, otherwise the project's
    /// resolved path.
    /// </summary>
    /// <remarks>
    /// The context wins because it is the value the workflow was started with and every turn in one run
    /// must agree on it; re-resolving from the project row would let an edit to <c>projects</c>
    /// mid-flight move a running task's working directory between its own turns. The project row is the
    /// fallback for a workflow started before the path was known, and for a row whose stored path was
    /// recorded against an older repository name.
    /// </remarks>
    internal async Task<string> ResolveWorkspacePathAsync(PromptStackContext context, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(context.WorkspacePath))
        {
            return WorkspacePathResolver.Normalize(context.WorkspacePath.Trim());
        }

        try
        {
            var project = await db.Projects
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == context.ProjectId, ct);

            if (project is not null)
            {
                return project.GetResolvedWorkingDirectory();
            }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            logger?.LogWarning(ex, "Could not resolve the working directory for project {ProjectId}", context.ProjectId);
        }

        // Last resort. WorkspacePathResolver.ReposRoot rather than "/workspace": the latter is the
        // sandbox's own fallback and would put commands in the same directory as every other project,
        // which is the exact multi-repo bug this feature removes. A path under ReposRoot at least fails
        // as "directory does not exist" — visible, and it does not corrupt another repo's tree.
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

    private async Task<TaskWorkingCheckpoint?> SafeLoadCheckpointAsync(PromptStackContext context, CancellationToken ct)
    {
        try
        {
            return await checkpoints.GetCheckpointAsync(context.JobId, context.TaskId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not load the checkpoint for job {JobId} task {TaskId}", context.JobId, context.TaskId);
            return null;
        }
    }

    private async Task<IReadOnlyList<string>> SafeLoadMemoriesAsync(PromptStackContext context, CancellationToken ct)
    {
        if (memories is null) return [];

        try
        {
            // Agent first, persona second. Memories are written under whichever identity the caller
            // resolved (MemoryContextResolver prefers the authenticated claim, then X-Agent-Id), and a
            // driver row and a persona row can both legitimately own lessons for the same project.
            // Reading both is what makes the layer useful after a persona change on the same task.
            var agentMemories = await memories.GetMemoriesForAgentAsync(context.AgentId, context.ProjectId, ct);
            var personaMemories = string.IsNullOrWhiteSpace(context.PersonaId) ||
                                  string.Equals(context.PersonaId, context.AgentId, StringComparison.OrdinalIgnoreCase)
                ? []
                : await memories.GetMemoriesForAgentAsync(context.PersonaId!, context.ProjectId, ct);

            var lines = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var memory in agentMemories.Concat(personaMemories))
            {
                var value = CollapseWhitespace(memory.Value);
                if (value.Length == 0 || !seen.Add($"{memory.Key}:{value}")) continue;

                // The spec's format is `[category/key]: value` — one bracket, slash-separated — which
                // reads as a path into the memory store and keeps the line to one scannable unit.
                // Category is free text on the model and an uncategorised lesson is common:
                // save_memory accepts a category but does not require one. Rendering a blank one as
                // "[/key]: value" puts an empty heading where a reader expects a label, so the slash
                // half is omitted entirely when there is nothing to say.
                var category = memory.Category?.Trim();
                var tag = string.IsNullOrEmpty(category) ? string.Empty : $"{category}/";

                lines.Add($"[{tag}{memory.Key.Trim()}]: {value}");
            }

            return CapMemories(lines);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not load memories for agent {AgentId} project {ProjectId}", context.AgentId, context.ProjectId);
            return [];
        }
    }

    /// <summary>
    /// Caps the rendered memory list.
    /// </summary>
    /// <remarks>
    /// <see cref="IAgentMemoryRepository.GetMemoriesForAgentAsync"/> is unbounded — Job B's contract
    /// deliberately returns every matching row. An agent that saves lessons diligently will eventually
    /// have more of them than the prompt can carry, and the memory layer would then crowd out the
    /// envelope and the sliding window, which are the sections that stop this turn from re-running
    /// <c>pwd</c>. The repository is ordered by category then recency, so keeping the head of the list
    /// keeps the most recently refreshed lessons in each category.
    /// <para>
    /// The count of what was dropped is reported rather than the omission being silent: an agent that
    /// knows it has unread notes will ask for them, and an agent that believes it saw everything will
    /// not.
    /// </para>
    /// <para>
    /// The cap is also what keeps Layer 5 from growing without bound and pushing Layer 6's content
    /// further from the prefix — a memory list that doubles in size changes every byte after it for
    /// every subsequent turn.
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
    /// Returned newest-first on purpose. The caller renders oldest-first (that is what reads correctly
    /// and what the wire payload requires), but the test that proves the cap works asserts on the
    /// newest message's presence and the oldest one's absence, which is unambiguous in either order.
    /// <para>
    /// The filter is applied <em>before</em> <c>Take</c>, not after. Filtering after the cap would mean
    /// "the last 15 rows, minus whichever ones turn out to be harness bookkeeping", so a session whose
    /// recent rows are artifact-heavy would yield a window of three messages when fifteen were asked
    /// for — and the agent would lose the thread precisely in the busy stretches where it needs it.
    /// </para>
    /// </remarks>
    internal async Task<(IReadOnlyList<SessionMessage> Window, int WindowSize)> SafeLoadWindowAsync(
        PromptStackContext context,
        TaskWorkingCheckpoint? checkpoint,
        CancellationToken ct)
    {
        var windowSize = ResolveWindowSize();

        var sessionId = await ResolveTurnSessionIdAsync(context, checkpoint, ct);
        if (sessionId is null)
        {
            return ([], windowSize);
        }

        try
        {
            // The content-prefix exclusions belong in the query, not in a post-filter.
            //
            // A prior turn's stored `prompt` row holds the flattened stack (layers 1–6), because that is
            // the shape the dispatcher persists and the shape a pre-split binary reads. Replaying it
            // would re-send the entire cached prefix as a user message — the prefix twice, plus the
            // persona and manifest a third time — which reintroduces through the history window exactly
            // the token burn the split removes from the system block.
            //
            // Filtering after Take would trade one bug for another: a session whose recent rows are
            // prompt-heavy would return a window of four messages when fifteen were asked for. Here the
            // cap applies to the set that is actually usable. StartsWith translates to a prefix LIKE on
            // both providers, so this stays server-side.
            var rows = await db.SessionMessages
                .AsNoTracking()
                .Where(m => m.SessionId == sessionId
                            && !ExcludedEventTypes.Contains(m.EventType)
                            && !m.Content.StartsWith(PromptStackWirePayload.PlatformLayerMarker)
                            && !m.Content.StartsWith(PromptStackWirePayload.EnvelopeMarker))
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
    /// the context carries one.</description></item>
    /// </list>
    /// <para>
    /// Step 3 is a metadata lookup because <c>agent_sessions</c> has no job id column: the dispatcher
    /// writes <c>jobId</c> into <c>metadata_json</c> and that is the only link that exists. It is
    /// filtered in memory rather than translated to SQL — <c>metadata_json</c> is jsonb and the
    /// in-memory provider the tests run on cannot translate a json path, so a SQL-side filter would make
    /// this the untested path. Sessions are read newest-first and the scan stops at the first match, so
    /// the cost is bounded by how many sessions are loaded, not by the table size.
    /// </para>
    /// <para>
    /// <strong>Why the checkpoint outranks the input.</strong> The input's session id is fixed when the
    /// workflow starts and never changes for the life of the run; the checkpoint is rewritten at every
    /// turn boundary. So once turn 2 dispatches a new session, the input still names turn 1's. Reading
    /// the input first would pin the sliding window to the first session forever: the agent would keep
    /// being handed turn 1's conversation and never see its own most recent work, which is the exact
    /// context-loss the window exists to prevent.
    /// </para>
    /// <para>
    /// The checkpoint is passed in rather than re-read here: the caller already loaded it for the
    /// envelope, and a second read of the same row inside one assembly is a query that buys nothing and
    /// can disagree with the first one.
    /// </para>
    /// </remarks>
    internal async Task<Guid?> ResolveTurnSessionIdAsync(
        PromptStackContext context,
        TaskWorkingCheckpoint? checkpoint,
        CancellationToken ct)
    {
        // 1. The checkpoint: the session that actually produced this task's last turn.
        if (Guid.TryParse(checkpoint?.TurnSessionId?.Trim(), out var fromCheckpoint) &&
            fromCheckpoint != Guid.Empty)
        {
            return fromCheckpoint;
        }

        // 2. The workflow input, when the starter knew it and no turn has recorded a newer one. This is
        // the first-turn path: the checkpoint does not exist yet, so the id the starter passed is the
        // only pointer to the session whose transcript this turn is continuing.
        if (Guid.TryParse(context.TurnSessionId?.Trim(), out var fromInput) && fromInput != Guid.Empty)
        {
            return fromInput;
        }

        if (!Guid.TryParse(context.JobId.Trim(), out var jobId)) return null;

        try
        {
            // Newest first, bounded: a job with a long history does not need every session row read to
            // find the current one, and an unbounded read here would grow with the job forever.
            var candidates = await db.AgentSessions
                .AsNoTracking()
                .Where(s => s.UpdatedAt >= DateTimeOffset.UtcNow.AddDays(-30))
                .OrderByDescending(s => s.UpdatedAt)
                .Take(200)
                .ToListAsync(ct);

            foreach (var session in candidates)
            {
                if (!MetadataMatchesJob(session.MetadataJson, jobId)) continue;
                if (!string.IsNullOrWhiteSpace(context.PersonaId) &&
                    !string.Equals(session.PersonaId, context.PersonaId, StringComparison.OrdinalIgnoreCase))
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
            logger?.LogWarning(ex, "Session lookup by job {JobId} failed", context.JobId);
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

    private async Task<ProjectJob?> SafeLoadJobAsync(PromptStackContext context, CancellationToken ct)
    {
        if (!Guid.TryParse(context.JobId.Trim(), out var jobId)) return null;

        try
        {
            return await db.ProjectJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            logger?.LogWarning(ex, "Could not load job {JobId}", context.JobId);
            return null;
        }
    }

    private async Task<Project?> SafeLoadProjectAsync(PromptStackContext context, CancellationToken ct)
    {
        try
        {
            return await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == context.ProjectId, ct);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            logger?.LogWarning(ex, "Could not load project {ProjectId}", context.ProjectId);
            return null;
        }
    }

    private async Task<Persona?> SafeLoadPersonaAsync(PromptStackContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.PersonaId)) return null;

        try
        {
            return await db.Personas.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == context.PersonaId, ct);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            logger?.LogWarning(ex, "Could not load persona {PersonaId}", context.PersonaId);
            return null;
        }
    }

    /// <summary>
    /// One roster entry, resolved to the fields the topology layer renders.
    /// </summary>
    internal sealed record RosterMember(
        string PersonaId,
        string Name,
        string Role,
        string? EscalationFor,
        string? TrustTier,
        bool IsSelf);

    /// <summary>
    /// Loads the project roster and maps each persona onto an escalation responsibility.
    /// </summary>
    /// <remarks>
    /// One joined query rather than a per-persona loop: a project with a dozen personas assigned would
    /// otherwise issue a dozen round trips inside prompt assembly, on the turn path, before the model
    /// has even been contacted. Ordering is by name so the rendered roster is byte-stable across turns
    /// and the layer stays cacheable — an unordered read would reshuffle the block and invalidate
    /// everything below it.
    /// </remarks>
    private async Task<IReadOnlyList<RosterMember>> SafeLoadRosterAsync(
        PromptStackContext context,
        CancellationToken ct)
    {
        try
        {
            var rows = await db.ProjectPersonas
                .AsNoTracking()
                .Where(pp => pp.ProjectId == context.ProjectId)
                .Join(db.Personas.AsNoTracking(),
                    pp => pp.PersonaId,
                    p => p.Id,
                    (pp, p) => new { p.Id, p.Name, p.Role, p.TrustTier })
                .OrderBy(x => x.Name)
                .ToListAsync(ct);

            return rows
                .Select(x => new RosterMember(
                    x.Id,
                    string.IsNullOrWhiteSpace(x.Name) ? x.Id : x.Name.Trim(),
                    string.IsNullOrWhiteSpace(x.Role) ? "Persona" : x.Role.Trim(),
                    EscalationForRole(x.Role),
                    x.TrustTier.ToString(),
                    IsSelf(x.Id, x.Name, context)))
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            logger?.LogWarning(ex, "Could not load the roster for project {ProjectId}", context.ProjectId);
            return [];
        }
    }

    /// <summary>
    /// Maps a role onto the escalation responsibility the agent should route to it.
    /// </summary>
    /// <remarks>
    /// Keyword-matched against the role text rather than driven by an enum, because
    /// <c>personas.role</c> is free-form prose authored by an operator ("Principal Architect",
    /// "Security Lead") and adding an enum would mean a migration plus a backfill to express something
    /// the roster already says. An unrecognised role renders with no escalation clause rather than a
    /// guessed one: naming the wrong escalation target is worse than naming none, because the agent
    /// will report "escalated to the architect" about a persona that cannot approve a schema change.
    /// </remarks>
    internal static string? EscalationForRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return null;

        var value = role.ToLowerInvariant();

        // Order matters twice over, and both cases are about two rows claiming the same category.
        //
        // Security precedes architect so that a "Security Architect" claims auth boundaries rather than
        // schema migrations. Rosters usually carry a plain architect as well, and if the narrower title
        // also claimed the architect category the agent would have two plausible targets for a
        // migration and would pick one arbitrarily — then report the escalation as routed.
        //
        // Architect and security both precede the seniority words, because "Principal Architect" and
        // "Security Lead" each contain a leadership token: matching on "lead" first would send a
        // vulnerability finding to whoever happens to carry the word.
        if (value.Contains("security", StringComparison.Ordinal)) return "auth boundaries and vulnerability findings";
        if (value.Contains("architect", StringComparison.Ordinal)) return "schema migrations, temporal and durability invariants, and public-contract changes";
        if (value.Contains("lead", StringComparison.Ordinal) || value.Contains("manager", StringComparison.Ordinal)) return "test deadlocks, 3-strike failures, and code reviews";
        if (value.Contains("operator", StringComparison.Ordinal) || value.Contains("product", StringComparison.Ordinal)) return "product scope and ambiguous requirements";

        return null;
    }

    /// <summary>
    /// True when this roster row is the agent building the prompt.
    /// </summary>
    /// <remarks>
    /// Matched on id and on name because <c>context.AgentId</c> is a driver id,
    /// <c>context.PersonaId</c> is a persona id, and either can legitimately be what the roster names.
    /// A name match is included because the two are frequently the same slug in practice
    /// (<c>rook</c>), and missing the self-case produces the self-escalation deadlock.
    /// </remarks>
    private static bool IsSelf(string personaId, string personaName, PromptStackContext context)
    {
        foreach (var candidate in new[] { context.PersonaId, context.AgentId })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var value = candidate.Trim();

            if (string.Equals(value, personaId, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrWhiteSpace(personaName) &&
                string.Equals(value, personaName, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// True for failures that mean "this section is unavailable" rather than "this code is wrong".
    /// </summary>
    /// <remarks>
    /// Narrow on purpose. The tempting version is <c>catch (Exception)</c> everywhere, and it turns a
    /// real defect — a mis-mapped entity, an NRE in the renderer — into a silently short prompt that the
    /// agent works around, which is a bug that never surfaces. These three are the failures a healthy
    /// process produces against an unhealthy database: a save/read conflict, a provider that cannot
    /// translate a query, and a missing row. Cancellation is excluded and re-thrown by every caller that
    /// needs to distinguish it.
    /// </remarks>
    private static bool IsRecoverable(Exception ex) =>
        ex is not OperationCanceledException &&
        ex is DbUpdateException or InvalidOperationException or KeyNotFoundException;
}
