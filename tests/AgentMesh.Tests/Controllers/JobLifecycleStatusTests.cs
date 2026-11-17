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
}
