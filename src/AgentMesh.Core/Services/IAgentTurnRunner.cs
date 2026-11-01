using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;

namespace AgentMesh.Core.Services;

/// <summary>
/// One assembled coding turn, handed to a runner by the macro workflow.
/// </summary>
/// <remarks>
/// <para>
/// The turn carries a composed <see cref="AssembledPromptStack"/> rather than a single prompt string,
/// and the difference is the whole point: a flat string cannot be split into a cacheable prefix and a
/// volatile envelope at the dispatch boundary, so every runner that received one had to either invent
/// its own system prompt (which is what the OpenAI adapter did) or stuff the entire stack into one user
/// turn (which is what the dispatcher did). Both produce a prompt whose leading bytes change every
/// turn, which is the cache-busting this job removes.
/// </para>
/// <para>
/// A runner must not append to the stack: everything the agent needs to avoid re-discovering its own
/// workspace has to be in the text before the first inference call, which is the entire point of the
/// pre-turn hooks.
/// </para>
/// </remarks>
/// <param name="Input">The task this turn belongs to.</param>
/// <param name="Stack">
/// The composed layers. Layers 1–5 travel as the system prefix, layer 6 as the primary user turn.
/// </param>
/// <param name="WorkspacePath">
/// Directory the turn operates in. Equal to <see cref="TaskExecutionInput.WorkspacePath"/> once the
/// composer has resolved it; carried separately because the reconciliation step needs it before the
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
    AssembledPromptStack Stack,
    string WorkspacePath,
    string? PersonaId,
    string? AgentId,
    string? TurnSessionId = null)
{
    /// <summary>
    /// The stack flattened into one prompt string.
    /// </summary>
    /// <remarks>
    /// For drivers that accept exactly one prompt argument and expose no message array — the subprocess
    /// adapters (<c>claude -p</c>, <c>copilot -p</c>). They get the same layers in the same order as the
    /// OpenAI adapter, so the same agent does not reason differently depending on which runner happened
    /// to take the job.
    /// </remarks>
    public string Prompt => Stack.FlattenToSinglePrompt();
}

/// <summary>
/// Executes one agent turn and reports where it ended.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam that makes the macro workflow testable. The worker has no LLM client of its own
/// and is not meant to gain one: turns run in a persona container, dispatched by the platform's
/// <c>JobTurnDispatcher</c> over SignalR, and the worker only needs to know how to start one and what
/// it concluded. Production binds this to the platform's dispatch path; tests bind it to a scripted
/// fake.
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
