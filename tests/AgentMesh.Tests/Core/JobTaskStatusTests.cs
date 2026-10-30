using AgentMesh.Core.Models;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Core;

/// <summary>
/// The task lifecycle graph: which moves are legal, and what a legacy row means.
/// </summary>
/// <remarks>
/// The graph is the only thing standing between "checklist" and "list of things". If it is wrong in
/// the permissive direction, an agent completes work it never started and the checklist stops being
/// evidence of anything. If it is wrong in the strict direction, an agent that legitimately needs to
/// reopen rejected review finds itself unable to express that and invents a new task instead.
/// Both failure modes are invisible to a test that only checks the happy path, so the whole matrix
/// is pinned here rather than sampled.
/// </remarks>
public class JobTaskStatusTests
{
    [Theory]
    // Forward moves.
    [InlineData(JobTaskStatus.Todo, JobTaskStatus.InProgress, true)]
    [InlineData(JobTaskStatus.InProgress, JobTaskStatus.AwaitingReview, true)]
    [InlineData(JobTaskStatus.InProgress, JobTaskStatus.Completed, true)]
    [InlineData(JobTaskStatus.AwaitingReview, JobTaskStatus.Completed, true)]
    // Rejected review goes back to work.
    [InlineData(JobTaskStatus.AwaitingReview, JobTaskStatus.InProgress, true)]
    [InlineData(JobTaskStatus.Completed, JobTaskStatus.InProgress, true)]
    [InlineData(JobTaskStatus.Completed, JobTaskStatus.Todo, true)]
    [InlineData(JobTaskStatus.InProgress, JobTaskStatus.Todo, true)]
    // Idempotent replay: an agent that retries a succeeded call must not be told it failed.
    [InlineData(JobTaskStatus.Todo, JobTaskStatus.Todo, true)]
    [InlineData(JobTaskStatus.InProgress, JobTaskStatus.InProgress, true)]
    [InlineData(JobTaskStatus.AwaitingReview, JobTaskStatus.AwaitingReview, true)]
    [InlineData(JobTaskStatus.Completed, JobTaskStatus.Completed, true)]
    // Skipping the review gate.
    [InlineData(JobTaskStatus.Todo, JobTaskStatus.AwaitingReview, false)]
    [InlineData(JobTaskStatus.Todo, JobTaskStatus.Completed, false)]
    // A finished task cannot be silently re-marked as merely "in review".
    [InlineData(JobTaskStatus.Completed, JobTaskStatus.AwaitingReview, false)]
    // Awaiting review cannot be demoted to untouched; that would erase the record that work happened.
    [InlineData(JobTaskStatus.AwaitingReview, JobTaskStatus.Todo, false)]
    public void CanTransition_MatchesTheDeclaredGraph(string from, string to, bool expected) =>
        JobTaskStatus.CanTransition(from, to).Should().Be(expected,
            $"{from} -> {to}");

    [Fact]
    public void CanTransition_RejectsAnUnknownState()
    {
        // The switch has an explicit `_ => false` arm. Without it, a new constant added to All would
        // silently become reachable from every state.
        JobTaskStatus.CanTransition("todo", "blocked").Should().BeFalse();
        JobTaskStatus.CanTransition("shipped", "completed").Should().BeFalse();
    }

    [Theory]
    [InlineData("todo", new[] { "todo", "in_progress" })]
    [InlineData("in_progress", new[] { "todo", "in_progress", "awaiting_review", "completed" })]
    [InlineData("awaiting_review", new[] { "in_progress", "awaiting_review", "completed" })]
    [InlineData("completed", new[] { "todo", "in_progress", "completed" })]
    public void AllowedNext_IsDerivedFromTheSameGraphTheTransitionCheckUses(string from, string[] expected) =>
        // AllowedNext exists so an error message and a refusal can never disagree about what is
        // permitted: both call CanTransition. If they drifted, an agent would be told a move is
        // allowed and then refused for making it.
        JobTaskStatus.AllowedNext(from).Should().Equal(expected);

    [Theory]
    [InlineData("in_progress", JobTaskStatus.InProgress)]
    [InlineData("IN_PROGRESS", JobTaskStatus.InProgress)]
    [InlineData("  completed  ", JobTaskStatus.Completed)]
    [InlineData("awaiting-review", JobTaskStatus.AwaitingReview)]
    [InlineData("awaiting review", JobTaskStatus.AwaitingReview)]
    public void Normalize_AcceptsTheSpellingsAnAgentActuallySends(string input, string expected) =>
        JobTaskStatus.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("done")]
    [InlineData("blocked")]
    [InlineData("in-progress-review")]
    public void Normalize_RejectsAnythingOutsideTheVocabulary(string? input) =>
        JobTaskStatus.Normalize(input).Should().BeNull();

    [Fact]
    public void TryNormalize_MirrorsNormalizeAndReportsSuccess()
    {
        JobTaskStatus.TryNormalize("Awaiting_Review", out var good).Should().BeTrue();
        good.Should().Be(JobTaskStatus.AwaitingReview);

        JobTaskStatus.TryNormalize("nonsense", out var bad).Should().BeFalse();
        bad.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, false, JobTaskStatus.Todo)]
    [InlineData(null, true, JobTaskStatus.Completed)]
    [InlineData(JobTaskStatus.InProgress, false, JobTaskStatus.InProgress)]
    [InlineData(JobTaskStatus.AwaitingReview, false, JobTaskStatus.AwaitingReview)]
    [InlineData(JobTaskStatus.Completed, true, JobTaskStatus.Completed)]
    public void Effective_DerivesStateFromIsCompletedWhenStatusIsAbsent(string? status, bool isCompleted, string expected) =>
        // Every row written before the Status field existed has null here. Deriving from
        // IsCompleted is what keeps a historical checklist reporting the state the Cockpit already
        // draws for it, instead of collapsing every completed task back to 'todo'.
        JobTaskStatus.Effective(status, isCompleted).Should().Be(expected);

    [Fact]
    public void Effective_PrefersAnExplicitStatusOverTheBoolean()
    {
        // An explicit write is newer information than the flag. This case is reachable when a task
        // was completed through the dashboard checkbox and then moved back to review by the tool.
        JobTaskStatus.Effective(JobTaskStatus.AwaitingReview, isCompleted: true)
            .Should().Be(JobTaskStatus.AwaitingReview);
    }

    [Fact]
    public void All_ContainsExactlyTheFourSpecifiedStates()
    {
        // The published MCP schema and every error hint are generated from this list, so an
        // accidental addition here silently widens what agents may send.
        JobTaskStatus.All.Should().Equal("todo", "in_progress", "awaiting_review", "completed");
    }

    [Fact]
    public void AllowedValuesHint_ListsEveryStateForTheErrorMessage() =>
        JobTaskStatus.AllowedValuesHint().Should().Be("'todo', 'in_progress', 'awaiting_review', 'completed'");

    [Fact]
    public void JobTaskDto_EffectiveStatusReflectsTheInstanceFields()
    {
        new JobTaskDto { IsCompleted = false }.EffectiveStatus.Should().Be(JobTaskStatus.Todo);
        new JobTaskDto { IsCompleted = true }.EffectiveStatus.Should().Be(JobTaskStatus.Completed);
        new JobTaskDto { Status = JobTaskStatus.InProgress }.EffectiveStatus.Should().Be(JobTaskStatus.InProgress);
    }
}
