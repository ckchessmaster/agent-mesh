using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;

namespace AgentMesh.Core.Services;

/// <summary>
/// Everything the composer needs to know to build one turn's prompt stack.
/// </summary>
/// <remarks>
/// <para>
/// The shape is the Job E contract: identity triple (agent, project, job), the task being worked, the
/// resolved working directory, the branch, and the objective. Two fields are additive to the original
/// spec sketch and both exist because the composer cannot do its job without them:
/// <see cref="PersonaId"/> selects Layer 2 (the persona row carries the identity prompt), and
/// <see cref="PendingReply"/> carries the chat signal that unblocked the turn, which must be rendered
/// into Layer 6 or the agent resumes without knowing why it was resumed.
/// </para>
/// <para>
/// <see cref="WorkspacePath"/> is optional-by-blank rather than required: the API starts a workflow
/// before the checkout necessarily exists, and the composer falls back to
/// <see cref="Project.GetResolvedWorkingDirectory"/> — which is the <c>/workspace/repos/{repo_name}</c>
/// convention — when the caller does not name a directory.
/// </para>
/// </remarks>
/// <param name="AgentId">Driver identity executing the turn (Layer 5's memory owner).</param>
/// <param name="ProjectId">Project slug; scopes Layer 3 and Layer 5.</param>
/// <param name="JobId">Job the task belongs to; scopes Layer 4 and the checkpoint.</param>
/// <param name="TaskId">The checklist item this turn works.</param>
/// <param name="WorkspacePath">
/// Resolved working directory, or blank to have the composer resolve it from the project row.
/// </param>
/// <param name="TargetBranch">Branch the turn commits to.</param>
/// <param name="UserPrompt">
/// The objective: the operator's message, or the next checklist item when there is none.
/// </param>
/// <param name="PersonaId">Persona whose identity prompt becomes Layer 2.</param>
/// <param name="PendingReply">Chat signal that unblocked this turn; null on a task's first turn.</param>
/// <param name="TurnSessionId">
/// The session the workflow was started against, when the starter knew it. Used only to locate the
/// sliding window, and only when no checkpoint has recorded a newer one.
/// </param>
public record PromptStackContext(
    string AgentId,
    string ProjectId,
    string JobId,
    string TaskId,
    string WorkspacePath,
    string TargetBranch,
    string UserPrompt,
    string? PersonaId = null,
    ChatMessageSignal? PendingReply = null,
    string? TurnSessionId = null)
{
    /// <summary>
    /// Derives a context from the durable workflow input.
    /// </summary>
    /// <remarks>
    /// The macro workflow hands its activity a <see cref="TaskExecutionInput"/>, which already carries
    /// the same identity fields. Deriving here rather than at every call site keeps the two records
    /// from drifting — a field added to the workflow input and not mapped here silently disappears from
    /// the prompt, which is a bug with no stack trace.
    /// </remarks>
    public static PromptStackContext FromTaskInput(
        TaskExecutionInput input,
        ChatMessageSignal? pendingReply = null)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new PromptStackContext(
            AgentId: input.AgentId,
            ProjectId: input.ProjectId,
            JobId: input.JobId,
            TaskId: input.TaskId,
            WorkspacePath: input.WorkspacePath,
            TargetBranch: input.TargetBranch,
            // The workflow input names no free-text objective: the task itself is the objective. The
            // composer renders the focus checklist item's title in this slot when it is blank.
            UserPrompt: string.Empty,
            PersonaId: input.PersonaId,
            PendingReply: pendingReply,
            TurnSessionId: input.TurnSessionId);
    }
}

/// <summary>
/// One prior turn of the session, already reduced to what a model needs to continue.
/// </summary>
/// <remarks>
/// Carried as a structured message rather than as flattened text because the payload contract requires
/// the sliding window to travel as real <c>user</c>/<c>assistant</c>/<c>tool</c> turns: a transcript
/// flattened into the system block is re-tokenized on every request and cannot be replayed by an
/// inference engine's conversation cache.
/// </remarks>
/// <param name="Role">Wire role: <c>user</c>, <c>assistant</c>, or <c>tool</c>.</param>
/// <param name="Content">Rendered content, already whitespace-collapsed and length-capped.</param>
/// <param name="EventType">
/// The originating <c>session_messages.event_type</c> (<c>prompt</c>, <c>thought</c>,
/// <c>tool_call</c>, <c>response</c>). Kept so a driver that distinguishes a thought from a final
/// answer can still tell them apart after the window crosses the wire.
/// </param>
public record TurnHistoryMessage(string Role, string Content, string? EventType = null);

/// <summary>
/// A composed prompt stack, split at the boundary the prefix cache needs.
/// </summary>
/// <remarks>
/// <para>
/// The split is the entire point of the type. <see cref="CachedSystemPrompt"/> holds the layers that
/// change slowly enough to be reused across turns, in order of decreasing stability;
/// <see cref="UserTaskEnvelope"/> holds the one block that changes every turn. Concatenating them into
/// a single string — which is what the pre-Job-E runner did — puts volatile content ahead of stable
/// content, and every byte after the first volatile one is then a cache miss on Ollama/vLLM and a
/// billed-input-token cost on a cloud API.
/// </para>
/// <para>
/// <see cref="History"/> is separate from both because it is neither: it grows monotonically within a
/// session and is appended after the envelope, which is the only position that keeps the prefix stable.
/// </para>
/// </remarks>
/// <param name="CachedSystemPrompt">
/// Layers 1 through 5 joined by a markdown rule — the prefix-cache target.
/// </param>
/// <param name="UserTaskEnvelope">
/// Layer 6: the turn-0 task manifest and environment recovery header. Dispatched as the primary
/// <c>user</c> message, never as a trailing system message.
/// </param>
/// <param name="History">
/// The sliding window, oldest first, as wire-role messages. Empty on a session's first turn.
/// </param>
/// <param name="WorkspacePath">
/// The resolved working directory — the value the shell activity defaults to, and the one named inside
/// the envelope.
/// </param>
/// <param name="EnvironmentNote">
/// The <c>[AUTOMATIC ENVIRONMENT RECOVERY NOTE]</c> block on its own, so a test can assert on it and so
/// the checkpoint writer can reuse the git facts it describes instead of shelling out twice.
/// </param>
/// <param name="Checkpoint">The checkpoint read at assembly time; null when the task has no prior state.</param>
/// <param name="HistoryMessageCount">How many messages the window contributed.</param>
/// <param name="MemoryCount">How many persistent memories Layer 5 injected.</param>
public record AssembledPromptStack(
    string CachedSystemPrompt,
    string UserTaskEnvelope,
    IReadOnlyList<TurnHistoryMessage> History,
    string WorkspacePath,
    string? EnvironmentNote = null,
    TaskWorkingCheckpoint? Checkpoint = null,
    int HistoryMessageCount = 0,
    int MemoryCount = 0)
{
    /// <summary>
    /// The stack flattened into one text prompt.
    /// </summary>
    /// <remarks>
    /// Not a convenience: subprocess drivers (<c>claude -p</c>, <c>copilot -p</c>, the Agy CLI) accept
    /// exactly one prompt string and have no message array to split. They still need the whole stack,
    /// and they still need it in the same order, or the same agent reasons differently depending on
    /// which runner happened to pick up its job. Envelope last, because recency is what a model weights
    /// most.
    /// </remarks>
    public string FlattenToSinglePrompt()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(CachedSystemPrompt).Append("\n\n");

        if (History is { Count: > 0 })
        {
            sb.Append("[RECENT CONVERSATION]\n");
            foreach (var message in History)
            {
                sb.Append("- [").Append(message.Role).Append("] ").Append(message.Content).Append('\n');
            }
            sb.Append('\n');
        }

        sb.Append(UserTaskEnvelope);
        return sb.ToString();
    }
}

/// <summary>
/// The prompt stack in its dispatchable form: the three parts a runner needs, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// This is the wire contract between the worker that composes a stack, the API that broadcasts it, and
/// the runner that serialises it into a model request. It is deliberately a separate type from
/// <see cref="AssembledPromptStack"/> rather than a serialisation of it: the assembled stack also
/// carries the checkpoint, the environment note and the counts the worker's own logging and
/// post-turn checkpointing read, and broadcasting those would put harness bookkeeping on a channel a
/// model never needs, while making the payload bigger for every agent in the broadcast group.
/// </para>
/// <para>
/// Every field is optional by design. A runner that receives a payload with no stack falls back to
/// building its own system prompt from the persona fields on the prompt event, which is what it does
/// today. That is what lets the worker, the API and the CLI binaries be deployed in any order: an old
/// CLI ignores the new field and behaves exactly as before, and a new CLI talking to an old API gets a
/// null stack and behaves exactly as before. A required field would make the rollout a lockstep
/// release of three artefacts, and the CLI binary in particular is published to
/// <c>dist/cli/</c> and shipped inside persona container images that are rebuilt on their own schedule.
/// </para>
/// </remarks>
/// <param name="CachedSystemPrompt">
/// Layers 1–5 — the prefix-cache target, to be sent as <c>messages[0]</c> with role
/// <c>system</c>.
/// </param>
/// <param name="UserTaskEnvelope">
/// Layer 6 — the turn-0 task manifest, to be sent as <c>messages[1]</c> with role <c>user</c>.
/// </param>
/// <param name="History">
/// The sliding window, oldest first, as wire-role messages. May be empty.
/// </param>
public record PromptStackWirePayload(
    string CachedSystemPrompt,
    string UserTaskEnvelope,
    IReadOnlyList<TurnHistoryMessage>? History = null)
{
    /// <summary>
    /// The literal first line of the turn-0 task envelope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declared once in Core because three separate components must agree on it: the composer writes it
    /// as the envelope's opening line, the runner recognises it to keep compaction from pruning the
    /// envelope, and the composer's own window query excludes prior turns' stored copies of it.
    /// </para>
    /// <para>
    /// A string literal rather than a marker attribute or an object reference, because the thing being
    /// identified has already been through a JSON round trip and a database write by the time anyone
    /// needs to recognise it. Content is the only channel that survives that trip intact.
    /// </para>
    /// </remarks>
    public const string EnvelopeMarker = "[TASK ENVELOPE & WORKING ENVIRONMENT]";

    /// <summary>
    /// The literal first line of Layer 1, and therefore of any flattened stack.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Needed alongside <see cref="EnvelopeMarker"/> because of what gets persisted. The dispatcher
    /// stores the turn's prompt on a <c>session_messages</c> row, and when a layered stack was sent that
    /// stored value is the <em>flattened</em> stack — layers 1 through 6 concatenated, since that is the
    /// shape a pre-split binary reads. A flattened stack begins with Layer 1, not with the envelope.
    /// </para>
    /// <para>
    /// So a later turn that replays its own session's rows would re-send the entire cached prefix as a
    /// user message: the prefix twice over, once as <c>messages[0]</c> and once inside the transcript,
    /// plus the persona, project manifest and memories a third time. That is the exact token burn this
    /// job exists to remove, re-introduced through the history window instead of the system block.
    /// </para>
    /// <para>
    /// Excluding on the leading marker is safe in the other direction too: an ordinary operator message
    /// never begins with a harness section header, so nothing a human typed is ever filtered out.
    /// </para>
    /// </remarks>
    public const string PlatformLayerMarker = "[LAYER 1: PLATFORM INVARIANTS]";
    /// <summary>
    /// Total content budget for the history carried on the wire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The dispatcher broadcasts a prompt to <c>Clients.All</c> as well as the two targeted groups, so
    /// every connected runner receives every byte of it. Per-message caps alone do not bound that: 15
    /// messages at the composer's 4,000-char ceiling is 60 KB of history multiplied by every agent on
    /// the mesh, per turn. This is the aggregate ceiling.
    /// </para>
    /// <para>
    /// Trimming takes from the oldest end, because the recent turns are the ones that answer "what was I
    /// doing" and the old ones are the ones the checkpoint and memories already summarise.
    /// </para>
    /// </remarks>
    public const int MaxHistoryContentChars = 48_000;

    /// <summary>
    /// Projects an assembled stack onto the wire form.
    /// </summary>
    public static PromptStackWirePayload? From(AssembledPromptStack? stack)
    {
        if (stack is null) return null;

        // Both text blocks must be present for the split to mean anything. A payload carrying an
        // envelope but no prefix would have a runner send messages[1] with no messages[0], and the
        // agent would get the task with none of its identity, conventions or memories — a worse prompt
        // than the one it builds for itself today. Returning null makes the runner take the legacy
        // path instead, which is the known-good behaviour.
        if (string.IsNullOrWhiteSpace(stack.CachedSystemPrompt) ||
            string.IsNullOrWhiteSpace(stack.UserTaskEnvelope))
        {
            return null;
        }

        return new PromptStackWirePayload(
            stack.CachedSystemPrompt,
            stack.UserTaskEnvelope,
            BoundHistory(stack.History));
    }

    /// <summary>
    /// Applies <see cref="MaxHistoryContentChars"/>, newest-first, reporting what was dropped.
    /// </summary>
    /// <remarks>
    /// The count is reported rather than the omission being silent for the same reason the memory cap
    /// reports its own: an agent that knows it is seeing a partial transcript will ask for the rest,
    /// and an agent that believes it saw everything will keep re-running work that is already done.
    /// </remarks>
    internal static IReadOnlyList<TurnHistoryMessage>? BoundHistory(IReadOnlyList<TurnHistoryMessage>? history)
    {
        if (history is null or { Count: 0 }) return history;

        var total = history.Sum(m => m.Content?.Length ?? 0);
        if (total <= MaxHistoryContentChars) return history;

        // The marker line is written into the result, so its cost has to come out of the budget before
        // the loop starts. Spending the full ceiling on messages and then appending the note on top
        // would make MaxHistoryContentChars a soft target that the function it governs routinely
        // overshoots — and the overshoot is invisible, because it is ~150 characters on a 48 KB payload.
        //
        // Sized against history.Count, the largest number the marker can ever print, so the reservation
        // is an upper bound rather than a guess. Sizing it against 0 would under-reserve by one
        // character per digit of the real count.
        var markerCost = OmissionMarker(history.Count).Length;
        var budget = MaxHistoryContentChars - markerCost;

        var kept = new List<TurnHistoryMessage>(history.Count);
        var dropped = 0;

        // Walk backwards so the newest turns are the ones that survive the budget.
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var cost = history[i].Content?.Length ?? 0;
            if (budget - cost < 0)
            {
                dropped = i + 1;
                break;
            }

            budget -= cost;
            kept.Insert(0, history[i]);
        }

        // The newest message is kept even when it alone is larger than the budget. Dropping it would
        // return a window containing nothing but an apology, which is strictly worse than an oversized
        // one: the composer caps each message well below this ceiling, so the only way to reach here is
        // a window written by a producer that does not, and the right answer to that is to carry the
        // turn that matters and let the transport deal with the size.
        if (kept.Count == 0)
        {
            kept.Add(history[^1]);
            dropped = history.Count - 1;
        }

        if (dropped == 0) return history;

        kept.Insert(0, new TurnHistoryMessage("user", OmissionMarker(dropped), "compaction_note"));

        return kept;
    }

    /// <summary>The omission marker's text, for a given number of dropped messages.</summary>
    /// <remarks>
    /// Extracted so the budget can be reserved against its worst-case length before the trim loop runs,
    /// rather than discovered afterwards when the total has already overshot.
    /// </remarks>
    private static string OmissionMarker(int droppedCount) =>
        $"… {droppedCount} older message(s) omitted from this turn's context window (the working " +
        "checkpoint and saved memories carry what they established) …";
}

/// <summary>
/// Composes a turn's context into the layered, cache-friendly prompt stack.
/// </summary>
/// <remarks>
/// <para>
/// This is the single composer for the platform. It replaces the earlier
/// <c>ITurnContextAssembler</c>, whose eight flat sections covered roughly the same ground in a
/// different order: keeping both would have meant two sources of truth for "what does an agent wake up
/// knowing", which is precisely the drift that produced the two failures this job exists to fix
/// (cache-busting monolithic prompts, and dropped task envelopes that leave an agent in conversational
/// waiting mode).
/// </para>
/// <para>
/// The layer order is fixed and is the contract, not an implementation detail:
/// <list type="number">
/// <item><description>Platform invariants — 100% static, cacheable across every agent and project.</description></item>
/// <item><description>Master persona prompt — static per agent identity.</description></item>
/// <item><description>Project manifest — static per repository.</description></item>
/// <item><description>Team topology and escalation paths — set per job.</description></item>
/// <item><description>Persistent agent memories — low volatility, scoped to (agent, project).</description></item>
/// <item><description>Turn-0 task envelope and environment manifest — dynamic, and the primary user turn.</description></item>
/// </list>
/// Layers 1–5 become <see cref="AssembledPromptStack.CachedSystemPrompt"/>; layer 6 becomes
/// <see cref="AssembledPromptStack.UserTaskEnvelope"/>.
/// </para>
/// <para>
/// Implementations must not throw on a section that fails to load. A memory table that is unreachable
/// or a git probe that times out degrades one layer; the turn still runs. An outage in a subsystem
/// taking task execution down with it is a worse failure than a turn with a shorter prompt.
/// </para>
/// </remarks>
public interface IPromptStackBuilder
{
    /// <summary>
    /// Builds the stack for one turn, running the pre-turn reads.
    /// </summary>
    Task<AssembledPromptStack> BuildPromptStackAsync(
        PromptStackContext context,
        CancellationToken ct = default);
}
