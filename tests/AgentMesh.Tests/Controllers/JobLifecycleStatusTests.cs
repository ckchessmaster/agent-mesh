using AgentMesh.Api.Controllers;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Controllers;

/// <summary>
/// How a job's heartbeat string becomes the lifecycle label the cockpit paints.
/// </summary>
/// <remarks>
/// <para>
/// This derivation is the only thing standing between a settled turn and a job that claims to be
/// executing. In the incident, the job's last heartbeat said <c>"Executing task 5/5: open the pull
/// request"</c> — written hours earlier by a turn that had already died — and that string is what the
/// dashboard rendered, all night, next to a session that said <c>thinking</c>.
/// </para>
/// <para>
/// So the ordering is the substance of the test, not the labels: <c>Stalled</c> has to be checked
/// <em>before</em> the "Executing task" substring, because a stalled job keeps carrying whatever it was
/// executing when it stalled. Any refactor that moves the substring checks up restores the lie quietly.
/// </para>
/// </remarks>
public class JobLifecycleStatusTests
{
    private static ProjectJob Job(
        string? heartbeat,
        ProjectJobStatus status = ProjectJobStatus.Active) =>
        new()
        {
            ProjectId = "proj-1",
            Title = "Standardized project setup",
            Status = status,
            ProgressHeartbeat = heartbeat
        };

    [Fact]
    public void Stalled_BeatsTheWorkItWasDoingWhenItStopped()
    {
        var job = Job("Stalled: the 110-minute ceiling expired. Was Executing task 5/5: open the pull request");

        ProjectJobsController.DeriveLifecycleStatus(job).Should().Be("Stalled");
    }

    [Fact]
    public void Stalled_BeatsAReviewGate()
    {
        // Nothing is waiting on a human in this state, which is precisely what distinguishes it from
        // AwaitingReview. A badge that reads "an approver is needed" sends the operator to the wrong
        // screen while the persona is not running at all.
        var job = Job("Stalled: while Awaiting pull request review on PR #91");

        ProjectJobsController.DeriveLifecycleStatus(job).Should().Be("Stalled");
    }

    [Fact]
    public void Stalled_MatchesTheMarker_CaseInsensitively()
    {
        ProjectJobsController.DeriveLifecycleStatus(Job("stalled: worker gave up")).Should().Be("Stalled");
    }

    [Fact]
    public void ARecoveredJobIsNoLongerStalled()
    {
        // Recovery overwrites the heartbeat with "Recovering: …". If the marker merely prefixed the old
        // text, the amber badge would survive the repair it exists to reflect.
        var job = Job("Recovering: resuming at task 3/3 - open the pull request");

        ProjectJobsController.DeriveLifecycleStatus(job).Should().NotBe("Stalled");
        ProjectJobsController.DeriveLifecycleStatus(job).Should().Be("InProgress");
    }

    [Theory]
    [InlineData("Awaiting plan approval from the lead", "AwaitingApproval")]
    [InlineData("Awaiting pull request review on PR #88", "AwaitingReview")]
    [InlineData("Exploring codebase for the entry points", "Planning")]
    [InlineData("Executing task 2/5: add the metrics route", "InProgress")]
    [InlineData("Plan approved; dispatching task 1", "InProgress")]
    [InlineData("Working on the checklist", "InProgress")]
    public void OrdinaryProgressStillMapsAsItDidBefore(string heartbeat, string expected)
    {
        // The stall marker is additive. Nothing that was rendering correctly before this feature should
        // move now, or the fix costs every healthy job its status label.
        ProjectJobsController.DeriveLifecycleStatus(Job(heartbeat)).Should().Be(expected);
    }

    [Fact]
    public void AnEmptyHeartbeatIsStillInProgress()
    {
        ProjectJobsController.DeriveLifecycleStatus(Job(null)).Should().Be("InProgress");
        ProjectJobsController.DeriveLifecycleStatus(Job("")).Should().Be("InProgress");
        ProjectJobsController.DeriveLifecycleStatus(Job("   ")).Should().Be("InProgress");
    }

    [Fact]
    public void ArchivedAndPendingJobsKeepTheirExistingMeaning()
    {
        // The marker can only ever be written to an Active job, so these two must not be reachable from
        // it in either direction.
        ProjectJobsController.DeriveLifecycleStatus(Job("Stalled: whatever", ProjectJobStatus.Archived))
            .Should().Be("Completed");
        ProjectJobsController.DeriveLifecycleStatus(Job("Stalled: whatever", ProjectJobStatus.Pending))
            .Should().BeNull();
    }

    [Fact]
    public void AStalledMarkerWithoutAWindowStillReadsAsStalled()
    {
        // MarkJobStalledAsync writes the prefix plus a reason sentence, and the reason can be blank, in
        // which case the line is bare. A trailing space must not defeat the badge.
        ProjectJobsController.DeriveLifecycleStatus(Job("Stalled:")).Should().Be("Stalled");
    }

    [Fact]
    public void AHaltedMarkerReadsAsBlocked()
    {
        // The cockpit's JobLifecycleStatus has always had a Blocked slot ("a gate is waiting on a
        // human") and DeriveLifecycleStatus never produced it: a workflow whose run completed in
        // failure — or whose halt text says the job is not progressing — rendered as plain
        // InProgress. PersistJobHaltAsync now writes the "Halted:" prefix, and this is the half
        // that turns it back into a badge.
        ProjectJobsController.DeriveLifecycleStatus(
            Job("Halted: no review on https://github.com/x/y/pull/9 within 7 days; halting."))
            .Should().Be("Blocked");
    }

    [Fact]
    public void AHaltedMarkerBeatsTheWorkQuotedInItsOwnReason()
    {
        // Same ordering reason as Stalled: halt reasons quote what the job was doing when the run
        // stopped ("Task 0 (Add ... + EF configuration) failed: Halted after 40 turns" is real text
        // from the incident), and the substring walk below would otherwise call it InProgress.
        var job = Job("Halted: Task 0 (Add ProjectJob.AttachmentsJson + EF configuration) failed: " +
                      "Halted after 40 turns without reaching a terminal state.");

        ProjectJobsController.DeriveLifecycleStatus(job).Should().Be("Blocked");
    }

    [Fact]
    public void StalledStillBeatsHalted()
    {
        // If a sweeper ever settles over a halted row, the stall marker is the fresher fact — and
        // Stalled offers /recover, while Blocked offers an approval.
        ProjectJobsController.DeriveLifecycleStatus(Job("Stalled: while Halted: waiting on review"))
            .Should().Be("Stalled");
    }

    [Theory]
    [InlineData("Awaiting Review: PR #98", "AwaitingReview")]
    [InlineData("Awaiting PR review on https://github.com/x/y/pull/98", "AwaitingReview")]
    public void ThePhrasingsTheGateCardTitleActuallyUsesReadAsAwaitingReview(string heartbeat, string expected)
    {
        // The original check matched "Awaiting pull request review" — a string nothing writes. The
        // persisted heartbeat is copied from the gate card's title ("Awaiting Review: PR #98"), and
        // the workflow's in-memory form is "Awaiting PR review on <url>". While the check missed both
        // spellings, a job parked seven days waiting for a human rendered as plain InProgress.
        ProjectJobsController.DeriveLifecycleStatus(Job(heartbeat)).Should().Be(expected);
    }
}
