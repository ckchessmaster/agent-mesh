using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;

namespace AgentMesh.Core.Services;

/// <summary>
/// One assembled coding turn, handed to a runner by the macro workflow.
/// </summary>
/// <remarks>
/// The prompt is produced by <see cref="ITurnContextAssembler"/> (identity, memories, task
/// envelope, checkpoint, environment note, sliding window) and is already complete. The runner must
/// not append to it: everything the agent needs to avoid re-discovering its own workspace has to be
/// in the text before the first inference call, which is the entire point of the pre-turn hooks.
/// </remarks>
/// <param name="Input">The task this turn belongs to.</param>
/// <param name="Prompt">The fully assembled prompt.</param>
/// <param name="WorkspacePath">
/// Directory the turn operates in. Equal to <see cref="TaskExecutionInput.WorkspacePath"/> once the
/// assembler has resolved it; carried separately because the reconciliation step needs it before the
/// prompt exists.
/// </param>
/// <param name="PersonaId">Persona whose prompt and credentials the turn uses.</param>
/// <param name="AgentId">Driver identity that executes the turn.</param>
/// <param name="TurnSessionId">
/// The turn session this dispatch creates, when the runner needs to pre-declare it. Normally null:
/// the platform's own dispatch path mints the session and reports it back, and guessing one ahead of
/// time would strand a session row that never ran.
/// </param>
public record AgentTurnRequest(
    TaskExecutionInput Input,
    string Prompt,
    string WorkspacePath,
    string? PersonaId,
    string? AgentId,
    string? TurnSessionId = null);

/// <summary>
/// Executes one agent turn and reports where it ended.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam that makes the macro workflow testable. The worker has no LLM client of its own
/// and is not meant to gain one: turns run in a persona container, dispatched by the API's
/// <c>JobTurnDispatcher</c> over SignalR, and the worker only needs to know how to start one and
/// what it concluded. Production binds this to the platform's dispatch path; tests bind it to a
/// scripted fake.
/// </para>
/// <para>
/// Implementations must be safe to call again after a timeout. Temporal retries an activity whose
/// <c>StartToCloseTimeout</c> elapses, and a naive implementation would start a second agent on the
/// same task while the first is still running. See
/// <see cref="AgentMesh.Worker.Activities.ExecutionActivities.RunAgentCodingTurnAsync"/> for how the
/// pairing is persisted to make a retry re-attach instead of re-dispatch.
/// </para>
/// </remarks>
public interface IAgentTurnRunner
{
    /// <summary>
    /// Runs (or re-attaches to) one turn and returns its outcome.
    /// </summary>
    /// <remarks>
    /// Throwing is reserved for "this turn cannot be started at all". A turn that started and then
    /// failed must come back as <see cref="TurnOutcomeStatus.Failed"/> or
    /// <see cref="TurnOutcomeStatus.StuckLoopDetected"/>, because an activity exception is retried by
    /// Temporal and a retried exception path re-runs the agent.
    /// </remarks>
    Task<TurnOutcome> RunTurnAsync(AgentTurnRequest request, CancellationToken ct = default);
}

/// <summary>
/// Builds the prompt for one coding turn: the context stack the agent wakes up to.
/// </summary>
/// <remarks>
/// Split out from <see cref="IAgentTurnRunner"/> because the two fail for different reasons and are
/// verified differently. Assembly is deterministic and pure-ish (database reads, string formatting)
/// so it is unit-testable against the in-memory provider with no runner at all; dispatch is
/// asynchronous and observable only through turn-session state. It is also the half that carries the
/// job's whole reason for existing — a turn assembled without the environment note is a turn where
/// the agent spends fifteen calls rediscovering <c>pwd</c>.
/// </remarks>
public interface ITurnContextAssembler
{
    /// <summary>
    /// Assembles the full prompt for a turn, running the pre-turn hooks.
    /// </summary>
    /// <param name="input">The task being worked.</param>
    /// <param name="pendingReply">
    /// A chat reply or review comment that unblocked this turn, injected last so it is the most
    /// recent thing the model reads. Null on the first turn of a task.
    /// </param>
    /// <param name="ct">Cancellation.</param>
    Task<AssembledTurnContext> AssembleAsync(
        TaskExecutionInput input,
        ChatMessageSignal? pendingReply = null,
        CancellationToken ct = default);
}

/// <summary>
/// The assembled context plus the facts the caller needs after assembly.
/// </summary>
/// <param name="Prompt">The complete prompt text, environment note included.</param>
/// <param name="WorkspacePath">
/// The resolved working directory the turn runs in — the value the shell activity will default to.
/// </param>
/// <param name="EnvironmentNote">
/// The <c>[AUTOMATIC ENVIRONMENT RECOVERY NOTE]</c> block on its own, so a caller can assert on it
/// without parsing the whole prompt, and so the checkpoint writer can reuse the git facts it
/// describes instead of shelling out a second time.
/// </param>
/// <param name="Checkpoint">
/// The checkpoint read at assembly time, or null when the task has no prior state.
/// </param>
/// <param name="HistoryMessageCount">
/// How many messages the sliding window contributed. Bounded by the window size; the number is what
/// a test asserts to prove the window was applied rather than the full history loaded.
/// </param>
/// <param name="MemoryCount">How many persistent memories were injected.</param>
public record AssembledTurnContext(
    string Prompt,
    string WorkspacePath,
    string? EnvironmentNote = null,
    TaskWorkingCheckpoint? Checkpoint = null,
    int HistoryMessageCount = 0,
    int MemoryCount = 0);
