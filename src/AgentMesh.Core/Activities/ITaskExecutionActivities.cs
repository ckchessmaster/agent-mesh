using AgentMesh.Core.Workflows;
using Temporalio.Activities;

namespace AgentMesh.Core.Activities;

/// <summary>
/// The macro-level activities the task-execution workflow invokes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why these are declared in Core.</strong> The workflow lives in the Worker, and the Worker
/// references Core, so it could reference concrete activity classes directly. Declaring the contract
/// here instead is what lets a test register a fake under the same activity names without loading the
/// worker's dependencies (database, shell, HTTP). It also matches how
/// <see cref="IDatabaseActivities"/> and <see cref="IExecutionActivities"/> already work.
/// </para>
/// <para>
/// <strong>The naming rule that makes this safe:</strong> Temporal resolves an activity by
/// <em>method name</em>, not by declaring type — the type parameter in
/// <c>ExecuteActivityAsync((ITaskExecutionActivities act) =&gt; act.RunAgentCodingTurnAsync(…))</c> is
/// only there to type the lambda. Two registered classes exposing the same method name collide, and a
/// workflow calling a name nobody registered fails at runtime rather than at compile time. Keep these
/// names unique across every activity type on the task queue.
/// </para>
/// </remarks>
public interface ITaskExecutionActivities
{
    /// <summary>
    /// Runs one coding turn: pre-turn reconciliation and context assembly, the agent's own
    /// multi-step loop, and the post-turn checkpoint.
    /// </summary>
    /// <remarks>
    /// Registered name: <c>RunAgentCodingTurn</c> (the SDK strips the <c>Async</c> suffix).
    /// <para>
    /// The whole local loop is inside this one activity on purpose. Recording each tool call as its
    /// own activity would put thousands of events in the workflow's history — Temporal's default
    /// limit is 51,200 events and 50 MB, and a single debugging session can approach that — which
    /// would eventually fail the workflow at the point of maximum invested work. The macro outcome is
    /// the only fact the workflow needs to branch on.
    /// </para>
    /// </remarks>
    /// <param name="input">The task being worked.</param>
    /// <param name="pendingReply">
    /// The chat reply or review comment that unblocked this turn, or null on an autonomous step.
    /// Passed through so the assembler can place it last in the prompt.
    /// </param>
    /// <remarks>
    /// <strong>No <see cref="CancellationToken"/> parameter, deliberately.</strong> Verified against a
    /// live test server on SDK 1.20.0: an activity whose method declares a cancellation token — defaulted
    /// or not — fails at <em>workflow</em> time with
    /// <c>NotSupportedException: Serialization and deserialization of 'System.IntPtr' instances is not
    /// supported</c>, because the expression-based call form materialises every parameter into the
    /// activity's payload and a token is not serializable. The token is read inside the implementation
    /// from <see cref="Temporalio.Activities.ActivityExecutionContext.CancellationToken"/> instead,
    /// which is also what makes the activity directly callable from a unit test with no context.
    /// <para>
    /// This is a trap for anything added to this interface later, which is why it is documented on the
    /// method rather than only in the implementation. <see cref="IExecutionActivities.ExecuteCommand"/>
    /// still declares a token because it is invoked directly (CLI runner, tools, tests) and never from a
    /// workflow expression; a workflow that ever calls it that way would hit the same failure.
    /// </para>
    /// </remarks>
    [Activity]
    Task<TurnOutcome> RunAgentCodingTurnAsync(
        TaskExecutionInput input,
        ChatMessageSignal? pendingReply);
}

/// <summary>
/// Git/PR activities driven by the workflow after an external decision.
/// </summary>
public interface IGitActivities
{
    /// <summary>
    /// Merges an approved pull request using server-side credentials.
    /// </summary>
    /// <remarks>
    /// Registered name: <c>MergePullRequest</c>.
    /// <para>
    /// Takes the PR URL rather than the whole input because the merge is a decision about a specific
    /// pull request, and a workflow that replays history must not re-derive which PR was approved
    /// from mutable task state.
    /// </para>
    /// <para>
    /// Idempotency is required, not optional: Temporal retries an activity whose
    /// <c>StartToCloseTimeout</c> elapses, and a merge call that times out after GitHub accepted it
    /// would otherwise be retried against an already-merged PR. The implementation treats
    /// "already merged" as success — see <c>GitActivities</c>.
    /// </para>
    /// </remarks>
    /// <returns>A human-readable result naming what was merged, for the workflow's summary.</returns>
    /// <remarks>
    /// No cancellation token parameter, for the serialization reason documented on
    /// <see cref="RunAgentCodingTurnAsync"/>.
    /// </remarks>
    [Activity]
    Task<string> MergePullRequestAsync(string prUrl);

    [Activity("MergeJobPullRequest")]
    Task<string> MergeJobPullRequestAsync(AgentMesh.Core.Workflows.MergePullRequestInput input) =>
        !string.IsNullOrWhiteSpace(input.PrUrl)
            ? MergePullRequestAsync(input.PrUrl)
            : throw new Temporalio.Exceptions.ApplicationFailureException("Merge requested without a pull request URL.", nonRetryable: true, errorType: "no_pr_url");
}
