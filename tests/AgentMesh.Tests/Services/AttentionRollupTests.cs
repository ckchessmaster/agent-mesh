using AgentMesh.Api.Controllers;
using AgentMesh.Api.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentMesh.Tests.Services;

/// <summary>
/// The cross-project pending-attention rollup behind <c>GET /api/attention</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What these tests are actually protecting.</strong> The consumer is a desktop notification
/// with a sound, so the interesting failure is not "a row is missing from a list" — it is a banner
/// firing for a job that is quietly working, or silence while a job sits blocked for three hours. Both
/// directions are pinned below: nearly every test that asserts an item exists has a sibling asserting a
/// near-miss does not.
/// </para>
/// <para>
/// <strong>Why the fixtures look repetitive.</strong> Each test builds its own in-memory context and
/// seeds exactly the rows that decision needs, because the cases are distinguished only by which of the
/// three sources (heartbeat, timeline card, ledger) is populated. A shared fixture would have to seed
/// all three for every case and would then prove nothing about the source that is absent.
/// </para>
/// <para>
/// Timestamps are absolute rather than <c>UtcNow</c>-relative: <see cref="AttentionItemDto.Since"/> is
/// the age an operator reads, and an assertion against a fixed base date fails loudly if the rollup
/// ever starts inventing its own.
/// </para>
/// </remarks>
public class AttentionRollupTests
{
    private static readonly DateTimeOffset Base = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NothingOpen_ReturnsEmptyRollup()
    {
        using var db = TestDbContextFactory.Create();
        SeedProject(db, "alpha", "Agent Mesh");
        db.ProjectJobs.Add(NewJob(projectId: "alpha", title: "Ship the bell",
            heartbeat: "Executing task #2: backend rollup"));
        await db.SaveChangesAsync();

        var rollup = await QueryAsync(db);

        rollup.Items.Should().BeEmpty();
        rollup.Count.Should().Be(0);
        rollup.GeneratedAt.Should().BeAfter(Base);
    }

    [Fact]
    public async Task FinishedJobs_NeverAppear_EvenWithAPendingPlanCard()
    {
        using var db = TestDbContextFactory.Create();
        SeedProject(db, "alpha");
        foreach (var status in new[] { ProjectJobStatus.Completed, ProjectJobStatus.Archived })
        {
            var jobId = Guid.NewGuid();
            db.ProjectJobs.Add(NewJob(jobId, projectId: "alpha", heartbeat: "Awaiting plan approval",
                status: status));
            db.JobTimelineCards.Add(NewCard(jobId, TimelineCardType.PlanApproval,
                TimelineCardStatus.PendingInput, "Plan Approval: Proposed Implementation Checklist"));
        }

        await db.SaveChangesAsync();

        (await QueryAsync(db)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task PendingJobs_AreIncluded_BecauseQueuedWorkCanAskQuestions()
    {
        using var db = TestDbContextFactory.Create();
        SeedProject(db, "alpha");
        db.ProjectJobs.Add(NewJob(heartbeat: "Awaiting user input on planning questions",
            status: ProjectJobStatus.Pending));
        await db.SaveChangesAsync();

        var item = (await QueryAsync(db)).Items.Should().ContainSingle().Subject;

        item.Type.Should().Be(AttentionTypes.DiscoveryQuestions);
    }

    [Fact]
    public async Task PlanGateRecordedOnlyOnTheHeartbeat_IsStillReported()
    {
        // The card-less path: manage_task set_checklist from the MCP surface writes the heartbeat and
        // leaves card creation to the orchestrated activity, which never ran on this job. A rollup that
        // only read cards would go silent on exactly the plan gate this feature exists for.
        using var db = TestDbContextFactory.Create();
        var updatedAt = Base.AddMinutes(12);
        SeedProject(db, "alpha");
        var jobId = Guid.NewGuid();
        db.ProjectJobs.Add(NewJob(jobId, heartbeat: "Awaiting plan approval — 6 tasks, awaiting go-ahead",
            updatedAt: updatedAt));
        await db.SaveChangesAsync();

        var item = (await QueryAsync(db)).Items.Should().ContainSingle().Subject;

        item.Id.Should().Be($"{jobId}:planApproval");
        item.Type.Should().Be(AttentionTypes.PlanApproval);
        item.JobId.Should().Be(jobId.ToString());
        item.CardId.Should().BeNull();
        item.PrUrl.Should().BeNull();
        item.PrNumber.Should().BeNull();
        item.Since.Should().Be(updatedAt);
        item.Detail.Should().Contain("6 tasks");
    }

    [Fact]
    public async Task PendingPlanCard_SuppliesTheChecklistProseAndIdentity()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        var createdAt = Base.AddMinutes(3);
        SeedProject(db, "alpha");
        db.ProjectJobs.Add(NewJob(jobId, heartbeat: "Awaiting plan approval", updatedAt: Base));
        db.JobTimelineCards.Add(NewCard(jobId, TimelineCardType.PlanApproval, TimelineCardStatus.PendingInput,
            "Plan Approval: Proposed Implementation Checklist",
            """
            {"tasks":[{"taskId":"1","title":"Backend: attention rollup","targetFile":"AttentionQueryService.cs","verificationMethod":"dotnet test"},
                      {"taskId":"2","title":"Frontend: notification service","targetFile":"notifications.ts","verificationMethod":"npm test"}],
             "reasoning":"One bell, one inbox, no new columns."}
            """,
            createdAt));
        await db.SaveChangesAsync();

        var item = (await QueryAsync(db)).Items.Should().ContainSingle().Subject;

        item.Title.Should().Be("Plan Approval: Proposed Implementation Checklist");
        item.Detail.Should().Contain("Backend: attention rollup").And.Contain("Frontend: notification service");
        // The checklist is the thing to read; the reasoning is a detail of the plan, not of the wait.
        item.Detail.Should().NotContain("no new columns");
        item.Since.Should().Be(createdAt,
            "the wait started when the card was raised, not when the job last moved");
    }

    [Fact]
    public async Task PendingDiscoveryCard_SuppliesTheQuestions()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        SeedProject(db, "alpha");
        db.ProjectJobs.Add(NewJob(jobId, heartbeat: "Awaiting discovery Q&A answers"));
        db.JobTimelineCards.Add(NewCard(jobId, TimelineCardType.DiscoveryQa, TimelineCardStatus.PendingInput,
            "Discovery Q&A: Clarify Task Requirements",
            """
            {"questions":[{"id":"q1","question":"Should the chime be configurable per project?","options":["yes","no"]},
                          {"id":"q2","question":"Sound on every wait or only the first?"}],
             "summary":"Two open calls before decomposing."}
            """));
        await db.SaveChangesAsync();

        var item = (await QueryAsync(db)).Items.Should().ContainSingle().Subject;

        item.Type.Should().Be(AttentionTypes.DiscoveryQuestions);
        item.Detail.Should().Contain("configurable per project").And.Contain("only the first");
    }

    [Fact]
    public async Task NewestPendingCardWins_WhenARejectedPlanWasRevised()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        SeedProject(db, "alpha");
        db.ProjectJobs.Add(NewJob(jobId, heartbeat: "Awaiting plan approval"));
        db.JobTimelineCards.Add(NewCard(jobId, TimelineCardType.PlanApproval, TimelineCardStatus.PendingInput,
            "Plan Approval: Proposed Implementation Checklist",
            """{"tasks":[{"taskId":"1","title":"First draft task"}],"reasoning":"v1"}""",
            Base.AddMinutes(-30)));
        var newest = NewCard(jobId, TimelineCardType.PlanApproval, TimelineCardStatus.PendingInput,
            "Plan Approval: Revised Implementation Checklist (Post-Feedback)",
            """{"tasks":[{"taskId":"1","title":"Second draft task"}],"reasoning":"v2"}""",
            Base.AddMinutes(-2));
        db.JobTimelineCards.Add(newest);
        await db.SaveChangesAsync();

        var item = (await QueryAsync(db)).Items.Should().ContainSingle().Subject;

        item.CardId.Should().Be(newest.Id);
        item.Title.Should().Contain("Revised");
        item.Detail.Should().Contain("Second draft task").And.NotContain("First draft task");
    }

    [Fact]
    public async Task InterruptedTurn_IsReportedWithTheReasonNotJustTheHeartbeat()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        SeedProject(db, "alpha");
        db.ProjectJobs.Add(NewJob(jobId, heartbeat: "Turn interrupted: operator pressed stop during task #4"));
        db.JobTimelineCards.Add(NewCard(jobId, TimelineCardType.TurnInterrupted, TimelineCardStatus.Active,
            "Turn Interrupted: operator pressed stop during task #4",
            """
            {"reason":"operator pressed stop during task #4","requestedBy":"human",
             "lastOutput":"Reading AttentionQueryService","interruptedAt":"2026-11-18T09:04:00+00:00",
             "availableActions":["resume","retry_step","realign"]}
            """));
        await db.SaveChangesAsync();

        var item = (await QueryAsync(db)).Items.Should().ContainSingle().Subject;

        item.Type.Should().Be(AttentionTypes.InterruptedTurn);
        item.Title.Should().Be("Turn Interrupted: operator pressed stop during task #4");
        item.Detail.Should().Contain("operator pressed stop").And.Contain("human");
    }

    [Fact]
    public async Task PrReview_IsReportedWithTheClickThroughFromTheReviewCard()
    {
        // The job row has no PR column, so the click-through target only exists on the gate card that
        // open_pull_request merges its linkage into. Without it the bell says "review me" with nowhere
        // to click.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        SeedProject(db, "alpha");
        db.ProjectJobs.Add(NewJob(jobId, heartbeat: "Awaiting pull request review (PR #4217)"));
        db.JobTimelineCards.Add(NewCard(jobId, TimelineCardType.PrReview, TimelineCardStatus.PendingInput,
            "Pull request awaiting review",
            """
            {"prNumber":4217,"prUrl":"https://github.com/ckchessmaster/agent-mesh/pull/4217",
             "headBranch":"mesh/job-1","baseBranch":"main","reviewerId":"astra","round":1,
             "source":"mcp:open_pull_request"}
            """));
        await db.SaveChangesAsync();

        var item = (await QueryAsync(db)).Items.Should().ContainSingle().Subject;

        item.Type.Should().Be(AttentionTypes.PrReview);
        item.PrUrl.Should().Be("https://github.com/ckchessmaster/agent-mesh/pull/4217");
        item.PrNumber.Should().Be(4217);
        item.Detail.Should().Contain("PR #4217");
        // A review gate is not an escalation: it asks for a review, and the kind must not degrade into
        // the generic bucket just because both are cards now.
        item.Id.Should().Be($"{jobId}:prReview");
    }

    [Fact]
    public async Task NewestReviewCardWins_AfterAPullRequestWasReopened()
    {
        // Cards are loaded newest-first, so the first pending gate is the current one. A job that went
        // through changes-requested and pushed a new head has two review cards, and the bell has to open
        // the pull request a reviewer has not refused yet.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        SeedProject(db, "alpha");
        db.ProjectJobs.Add(NewJob(jobId, heartbeat: "Awaiting pull request review (PR #142)"));
        db.JobTimelineCards.Add(NewCard(jobId, TimelineCardType.PrReview, TimelineCardStatus.Resolved,
            "Pull request awaiting review",
            """{"prNumber":100,"prUrl":"https://example.invalid/pull/100"}""", Base.AddMinutes(-40)));
        db.JobTimelineCards.Add(NewCard(jobId, TimelineCardType.PrReview, TimelineCardStatus.PendingInput,
            "Pull request awaiting review",
            """{"prNumber":142,"prUrl":"https://example.invalid/pull/142"}""", Base.AddMinutes(-5)));
        await db.SaveChangesAsync();

        var item = (await QueryAsync(db)).Items.Should().ContainSingle().Subject;

        item.PrNumber.Should().Be(142);
        item.PrUrl.Should().Be("https://example.invalid/pull/142");
    }

    [Fact]
    public async Task AReviewGateWithoutAHeartbeatSayingSo_IsNotAnnounced()
    {
        // Deliberate, and the reason prReview is not in the pending-card ranking: only the workflow's own
        // gate card moves when a pull request merges, so a job that went back to implementing can keep a
        // stale PendingInput card behind. Ringing the bell for that is how a notification surface loses
        // its operator. The heartbeat naming the review is the live signal.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        SeedProject(db, "alpha");
        db.ProjectJobs.Add(NewJob(jobId, heartbeat: "Executing task #5: address review feedback"));
        db.JobTimelineCards.Add(NewCard(jobId,
            TimelineCardType.PrReview, TimelineCardStatus.PendingInput, "Pull request awaiting review"));
        await db.SaveChangesAsync();

        (await QueryAsync(db)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task UnansweredOperatorEscalation_IsReportedAndDatedFromTheCard()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        var askedAt = Base.AddMinutes(45);
        SeedProject(db, "alpha");
        // No heartbeat: a parked post_job_message leaves the heartbeat saying whatever the task was doing.
        db.ProjectJobs.Add(NewJob(jobId, heartbeat: null));
        db.JobTimelineCards.Add(NewCard(jobId, TimelineCardType.Escalation3R, TimelineCardStatus.PendingInput,
            "Escalation from @rook to @operator",
            """{"content":"@operator Result: hit the 3-strike limit on the rollup tests.\nReasoning: reverted.\nRisk: need guidance on pooling.","requiresReply":true,"targetPersona":"operator","senderPersona":"rook","source":"mcp:post_job_message"}""",
            askedAt));
        await db.SaveChangesAsync();

        var item = (await QueryAsync(db)).Items.Should().ContainSingle().Subject;

        item.Type.Should().Be(AttentionTypes.Escalation);
        item.Id.Should().Be($"{jobId}:escalation");
        item.Since.Should().Be(askedAt);
        item.CardId.Should().NotBeEmpty("the click-through target is the card the operator has to answer");
        // The mention the writers prepend is not the operator's problem to read twice.
        item.Title.Should().Be("Result: hit the 3-strike limit on the rollup tests.");
        item.Detail.Should().Contain("need guidance on pooling").And.NotContain("\n");
    }

    [Fact]
    public async Task Escalation_IsClearedOnceTheOperatorAnswersOnTheCard()
    {
        // The card comment route flips an Escalation3R out of PendingInput; that flip is the whole
        // closure signal now, which is why no "has a human replied since" read survives in the service.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        SeedProject(db, "alpha");
        db.ProjectJobs.Add(NewJob(jobId, heartbeat: null));
        db.JobTimelineCards.Add(NewCard(jobId, TimelineCardType.Escalation3R, TimelineCardStatus.Resolved,
            "Escalation from @rook to @operator",
            """{"content":"Which branch should I target?","requiresReply":true,"targetPersona":"operator"}""",
            Base.AddMinutes(1)));
        await db.SaveChangesAsync();

        (await QueryAsync(db)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task TwoOpenAsksOnOneJob_StillProduceOneItem_AndTheNewestOneWins()
    {
        // A bell that lists the same job twice is noise, and the newest ask is the one blocking the turn.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        SeedProject(db, "alpha");
        db.ProjectJobs.Add(NewJob(jobId, heartbeat: null));
        db.JobTimelineCards.Add(NewCard(jobId, TimelineCardType.Escalation3R, TimelineCardStatus.PendingInput,
            "Escalation from @rook to @operator",
            """{"content":"First question.","requiresReply":true,"targetPersona":"operator"}""",
            Base.AddMinutes(1)));
        db.JobTimelineCards.Add(NewCard(jobId, TimelineCardType.Escalation3R, TimelineCardStatus.PendingInput,
            "Escalation from @rook to @operator",
            """{"content":"Second question.","requiresReply":true,"targetPersona":"operator"}""",
            Base.AddMinutes(6)));
        await db.SaveChangesAsync();

        var item = (await QueryAsync(db)).Items.Should().ContainSingle().Subject;

        item.Type.Should().Be(AttentionTypes.Escalation);
        item.Title.Should().Be("Second question.");
        item.Since.Should().Be(Base.AddMinutes(6));
    }

    [Fact]
    public async Task ACardThatSaysItIsNotARequest_IsNotAWait()
    {
        // post_job_message without requires_reply writes a TaskStep card, not an escalation, but a writer
        // that addresses the operator and explicitly declines a reply must not pin a permanent item to a
        // job waiting on nobody.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        SeedProject(db, "alpha");
        db.ProjectJobs.Add(NewJob(jobId, heartbeat: null));
        db.JobTimelineCards.Add(NewCard(jobId, TimelineCardType.Escalation3R, TimelineCardStatus.PendingInput,
            "Update from @rook",
            """{"content":"FYI, the migration is idempotent.","requiresReply":false,"targetPersona":"operator"}""",
            Base.AddMinutes(1)));
        await db.SaveChangesAsync();

        (await QueryAsync(db)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task AskAddressedToAnotherPersona_IsNotTheOperatorsProblem()
    {
        // requires_reply parks the addressed agent, not the operator: a desktop banner about an internal
        // consult is the noise that makes an operator mute the whole surface.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        SeedProject(db, "alpha");
        db.ProjectJobs.Add(NewJob(jobId, heartbeat: null));
        db.JobTimelineCards.Add(NewCard(jobId, TimelineCardType.Escalation3R, TimelineCardStatus.PendingInput,
            "Escalation from @rook to @astra",
            """{"content":"Astra, is this the right durability guarantee?","requiresReply":true,"targetPersona":"astra"}""",
            Base.AddMinutes(1)));
        await db.SaveChangesAsync();

        (await QueryAsync(db)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanningQuestionsAddressedToAPersona_AreNotTheOperatorsProblem()
    {
        // Same distinction as the ledger, read off the decorated heartbeat the MCP surface writes.
        using var db = TestDbContextFactory.Create();
        SeedProject(db, "alpha");
        db.ProjectJobs.Add(NewJob(heartbeat: "Awaiting input from @astra on planning questions"));
        await db.SaveChangesAsync();

        (await QueryAsync(db)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task CardPredicate_ReadsExactlyTheWaitBearingCardTypes()
    {
        // The other card types (TaskStep, JobInitialized, InternalConsultation) are the common case by
        // volume and carry no wait, so they must never leave the database on a polled endpoint.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        foreach (var type in new[]
                 {
                     TimelineCardType.PlanApproval, TimelineCardType.DiscoveryQa,
                     TimelineCardType.TurnInterrupted, TimelineCardType.Escalation3R,
                     TimelineCardType.PrReview
                 })
        {
            db.JobTimelineCards.Add(NewCard(jobId, type, TimelineCardStatus.PendingInput, $"{type} gate"));
        }

        db.JobTimelineCards.Add(NewCard(jobId, TimelineCardType.TaskStep, TimelineCardStatus.PendingInput, "noise"));
        db.JobTimelineCards.Add(NewCard(jobId, TimelineCardType.JobInitialized, TimelineCardStatus.Active, "noise"));
        db.JobTimelineCards.Add(NewCard(Guid.NewGuid(), TimelineCardType.PlanApproval,
            TimelineCardStatus.PendingInput, "another job"));
        await db.SaveChangesAsync();

        var loaded = await db.JobTimelineCards.AsNoTracking()
            .Where(AttentionQueryService.CardPredicate([jobId.ToString()]))
            .Select(c => c.CardType)
            .ToListAsync();

        loaded.Should().BeEquivalentTo(new[]
        {
            TimelineCardType.PlanApproval, TimelineCardType.DiscoveryQa,
            TimelineCardType.TurnInterrupted, TimelineCardType.Escalation3R,
            TimelineCardType.PrReview
        });
    }

    [Fact]
    public async Task BusyHeartbeat_SuppressesAStalePendingCard()
    {
        // The guard against notification spam: nothing in the workflow resolves a TurnInterrupted card
        // to a terminal status, so a resumed job must not keep a row in the inbox forever.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        SeedProject(db, "alpha");
        db.ProjectJobs.Add(NewJob(jobId, heartbeat: "Executing task #3: wire the bell"));
        db.JobTimelineCards.Add(NewCard(jobId, TimelineCardType.TurnInterrupted, TimelineCardStatus.Active,
            "Turn Interrupted: operator pressed stop"));
        db.JobTimelineCards.Add(NewCard(jobId, TimelineCardType.DiscoveryQa, TimelineCardStatus.PendingInput,
            "Discovery Q&A: Clarify Task Requirements"));
        await db.SaveChangesAsync();

        (await QueryAsync(db)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task BusyHeartbeat_StillSurfacesAnUnansweredAsk()
    {
        // The mirror of the test above, and the reason the escalation card is not simply last in line:
        // parking the session does not rewrite the heartbeat, so "Executing task #3" here is a job that is
        // blocked on a human while looking busy.
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        SeedProject(db, "alpha");
        db.ProjectJobs.Add(NewJob(jobId, heartbeat: "Executing task #3: wire the bell"));
        db.JobTimelineCards.Add(NewCard(jobId, TimelineCardType.Escalation3R, TimelineCardStatus.PendingInput,
            "Escalation from @rook to @operator",
            """{"content":"Do you want the chime on by default?","requiresReply":true,"targetPersona":"operator"}""",
            Base.AddMinutes(2)));
        await db.SaveChangesAsync();

        (await QueryAsync(db)).Items.Should().ContainSingle().Subject
            .Type.Should().Be(AttentionTypes.Escalation);
    }

    [Fact]
    public async Task TwoWaitsOnOneJob_ReportTheOneTheOperatorCanClearFirst()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        SeedProject(db, "alpha");
        db.ProjectJobs.Add(NewJob(jobId, heartbeat: null));
        db.JobTimelineCards.Add(NewCard(jobId, TimelineCardType.PlanApproval, TimelineCardStatus.PendingInput,
            "Plan Approval: Proposed Implementation Checklist"));
        db.JobTimelineCards.Add(NewCard(jobId, TimelineCardType.Escalation3R, TimelineCardStatus.PendingInput,
            "Escalation from @rook to @operator",
            """{"content":"Also: is the target branch right?","requiresReply":true,"targetPersona":"operator"}""",
            Base.AddMinutes(1)));
        await db.SaveChangesAsync();

        var item = (await QueryAsync(db)).Items.Should().ContainSingle().Subject;

        item.Type.Should().Be(AttentionTypes.PlanApproval);
        item.Id.Should().Be($"{jobId}:planApproval");
    }

    [Fact]
    public async Task RollupSpansProjects_AndShowsTheOldestWaitFirst()
    {
        using var db = TestDbContextFactory.Create();
        SeedProject(db, "alpha", "Agent Mesh");
        SeedProject(db, "beta", "Legacy Monolith");

        db.ProjectJobs.Add(NewJob(projectId: "alpha", title: "Ship the bell",
            heartbeat: "Awaiting plan approval", updatedAt: Base.AddHours(2)));
        db.ProjectJobs.Add(NewJob(projectId: "beta", title: "Fix the reaper",
            heartbeat: "Awaiting pull request review (PR #12)", updatedAt: Base.AddHours(1)));
        db.ProjectJobs.Add(NewJob(projectId: "beta", title: "Quiet job",
            heartbeat: "Executing task #1: read the code", updatedAt: Base));
        await db.SaveChangesAsync();

        var rollup = await QueryAsync(db);

        rollup.Count.Should().Be(2);
        rollup.Items.Should().HaveCount(2);
        rollup.Items.Select(i => i.JobTitle).Should().ContainInOrder("Fix the reaper", "Ship the bell");
        rollup.Items.Select(i => i.ProjectName).Should().ContainInOrder("Legacy Monolith", "Agent Mesh");
        rollup.Items.Select(i => i.ProjectId).Should().ContainInOrder("beta", "alpha");
        rollup.Items.Select(i => i.Since).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task MissingProjectRow_DoesNotDropTheItem()
    {
        // A job whose project row is gone (or whose id never matched one) is still waiting on someone.
        // Losing the item would lose the notification; only the display name may be absent.
        using var db = TestDbContextFactory.Create();
        db.ProjectJobs.Add(NewJob(projectId: "ghost", heartbeat: "Awaiting plan approval"));
        await db.SaveChangesAsync();

        var item = (await QueryAsync(db)).Items.Should().ContainSingle().Subject;

        item.ProjectId.Should().Be("ghost");
        item.ProjectName.Should().BeNull();
    }

    [Theory]
    // Every literal the writers in this repo put on a heartbeat, decorated as they decorate it.
    [InlineData("Awaiting plan approval", AttentionTypes.PlanApproval)]
    [InlineData("Checklist registered. Awaiting plan approval.", AttentionTypes.PlanApproval)]
    [InlineData("Awaiting plan approval (6 tasks, 3 target files)", AttentionTypes.PlanApproval)]
    [InlineData("Awaiting discovery Q&A answers", AttentionTypes.DiscoveryQuestions)]
    [InlineData("Awaiting user input on planning questions", AttentionTypes.DiscoveryQuestions)]
    [InlineData("Awaiting input from @operator on planning questions", AttentionTypes.DiscoveryQuestions)]
    [InlineData("Turn interrupted: 3-strike limit reached", AttentionTypes.InterruptedTurn)]
    [InlineData("Awaiting pull request review (PR #999)", AttentionTypes.PrReview)]
    [InlineData("awaiting PULL REQUEST review", AttentionTypes.PrReview)]
    public void HeartbeatNamesAWait(string heartbeat, string expected)
        => AttentionQueryService.ClassifyHeartbeat(heartbeat)?.ToWireName().Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Executing task #3: backend rollup")]
    [InlineData("Plan approved. Executing task #2")]
    [InlineData("Exploring codebase")]
    [InlineData("Awaiting input from @astra on planning questions")]
    [InlineData("Completed 6 of 6 tasks")]
    public void HeartbeatDescribesWorkOrAnInternalConsult(string? heartbeat)
        => AttentionQueryService.ClassifyHeartbeat(heartbeat).Should().BeNull();

    [Theory]
    // A card's PendingInput status is the wait; the payload only says who has to answer. So only an
    // explicit other target, or an explicit "this is not a request", suppresses the item — a missing or
    // unreadable payload must never swallow a real park.
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("{}", true)]
    [InlineData("not json", true)]
    [InlineData("[1,2,3]", true)]
    [InlineData("""{"content":"hi"}""", true)]
    [InlineData("""{"targetPersona":"operator"}""", true)]
    [InlineData("""{"targetPersona":"@Operator"}""", true)]
    [InlineData("""{"targetPersona":null}""", true)]
    [InlineData("""{"targetPersona":""}""", true)]
    [InlineData("""{"requiresReply":true,"targetPersona":"astra"}""", false)]
    [InlineData("""{"requiresReply":true,"targetPersona":"@sorren"}""", false)]
    [InlineData("""{"requiresReply":false,"targetPersona":"operator"}""", false)]
    [InlineData("""{"requiresReply":"false","targetPersona":"operator"}""", false)]
    public void OnlyAnAskAddressedElsewhere_IsSuppressed(string? payload, bool expected)
        => AttentionQueryService.EscalationAwaitsOperator(payload).Should().Be(expected);

    [Fact]
    public void PayloadProse_DegradesInsteadOfThrowing()
    {
        AttentionQueryService.ReadPayloadStrings(null, "tasks", "title", "reasoning").Should().BeNull();
        AttentionQueryService.ReadPayloadStrings("{", "tasks", "title", "reasoning").Should().BeNull();
        AttentionQueryService.ReadPayloadStrings("""{"tasks":[]}""", "tasks", "title", "reasoning")
            .Should().BeNull("an empty checklist has no prose to show, so the caller keeps its fallback");
        AttentionQueryService.ReadPayloadStrings("""{"tasks":"nope"}""", "tasks", "title", "reasoning")
            .Should().BeNull();
        AttentionQueryService.ReadPayloadStrings("""{"tasks":[{"title":42}]}""", "tasks", "title", "reasoning")
            .Should().BeNull();
        AttentionQueryService.ReadPayloadStrings("""{"reasoning":"because"}""", "tasks", "title", "reasoning")
            .Should().Be("because");
        AttentionQueryService.ReadPayloadStrings("""{"reason":"stalled","requestedBy":"human"}""",
                null, null, "reason", "requestedBy")
            .Should().Be("stalled · human");
    }

    [Fact]
    public void DetailIsFlattenedAndCappedSoABannerStaysABanner()
    {
        AttentionQueryService.Truncate("  first line \r\n  second line  ")
            .Should().Be("first line second line");
        AttentionQueryService.Truncate(null).Should().BeNull();
        AttentionQueryService.Truncate("   ").Should().BeNull();

        var longText = AttentionQueryService
            .Truncate(new string('x', AttentionQueryService.DetailCharLimit * 2));

        longText!.Length.Should().Be(AttentionQueryService.DetailCharLimit);
        longText.Should().EndWith("…");
    }

    private static Task<AttentionRollupDto> QueryAsync(AgentMeshDbContext db)
        => new AttentionQueryService(db, NullLogger<AttentionQueryService>.Instance).QueryAsync();

    private static void SeedProject(AgentMeshDbContext db, string id, string? name = null)
        => db.Projects.Add(new Project { Id = id, Name = name ?? id });

    private static ProjectJob NewJob(
        Guid? id = null,
        string projectId = "alpha",
        string title = "Job",
        string? heartbeat = null,
        ProjectJobStatus status = ProjectJobStatus.Active,
        DateTimeOffset? updatedAt = null)
        => new()
        {
            Id = id ?? Guid.NewGuid(),
            ProjectId = projectId,
            Title = title,
            Description = title,
            Status = status,
            ProgressHeartbeat = heartbeat,
            TasksJson = "[]",
            GitBranchName = "mesh/job-x",
            CreatedAt = Base,
            UpdatedAt = updatedAt ?? Base
        };

    private static JobTimelineCard NewCard(
        Guid jobId,
        TimelineCardType type,
        TimelineCardStatus status,
        string title,
        string payload = "{}",
        DateTimeOffset? createdAt = null)
        => new()
        {
            JobId = jobId.ToString(),
            CardType = type,
            Status = status,
            Title = title,
            StructuredPayloadJson = payload,
            CreatedAt = createdAt ?? Base,
            UpdatedAt = createdAt ?? Base
        };
}

/// <summary>
/// The HTTP surface: <c>GET /api/attention</c>.
/// </summary>
/// <remarks>
/// Driven by constructing the controller, matching the rest of this suite — the repo has no
/// <c>WebApplicationFactory</c> harness. The route template and the authorization attributes are
/// asserted too, because both are part of the contract the Cockpit compiles against and nothing else
/// checks them: an accidental <c>[AllowAnonymous]</c> here would quietly widen a cross-project read of
/// escalation bodies to any unauthenticated caller.
/// </remarks>
public class AttentionRollupEndpointTests
{
    [Fact]
    public async Task GetRollup_ReturnsTheAggregateBody()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        db.Projects.Add(new Project { Id = "alpha", Name = "Agent Mesh" });
        db.ProjectJobs.Add(new ProjectJob
        {
            Id = jobId,
            ProjectId = "alpha",
            Title = "Ship the bell",
            Status = ProjectJobStatus.Active,
            ProgressHeartbeat = "Awaiting plan approval",
            TasksJson = "[]",
            GitBranchName = "mesh/job-x",
            UpdatedAt = new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero)
        });
        await db.SaveChangesAsync();

        var controller = new AttentionController(
            new AttentionQueryService(db, NullLogger<AttentionQueryService>.Instance));

        var result = await controller.GetRollup(CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var rollup = ok.Value.Should().BeOfType<AttentionRollupDto>().Subject;
        rollup.Count.Should().Be(1);
        rollup.Items.Should().ContainSingle().Which.Type.Should().Be(AttentionTypes.PlanApproval);
    }

    [Fact]
    public void RouteAndAuthorizationArePartOfTheContract()
    {
        var controllerType = typeof(AttentionController);

        controllerType.GetCustomAttributes(typeof(RouteAttribute), false)
            .Cast<RouteAttribute>().Single().Template.Should().Be("api/attention");

        controllerType.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Should().NotBeEmpty();
        controllerType.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true).Should().BeEmpty();
    }
}

/// <summary>
/// Renders the rollup's queries against the real provider.
/// </summary>
/// <remarks>
/// <see cref="TestDbContextFactory.Postgres"/> points at a port nothing listens on, which is the point:
/// <c>ToQueryString()</c> compiles the expression tree to SQL without opening a connection, so this
/// catches a predicate the InMemory provider would happily evaluate on the client and Npgsql would
/// reject at request time. Without it, every other test in this file could pass while
/// <c>GET /api/attention</c> answers a 500 in production.
/// </remarks>
public class AttentionRollupSqlTranslationTests
{
    [Fact]
    public void OpenJobsPredicate_RendersAgainstProjectJobs()
    {
        using var db = TestDbContextFactory.Postgres();

        var sql = db.ProjectJobs.AsNoTracking()
            .Where(AttentionQueryService.OpenJobsPredicate)
            .ToQueryString();

        sql.Should().Contain("FROM project_jobs");
        sql.Should().Contain("WHERE");
        sql.Should().Contain("status");
    }

    [Fact]
    public void CardPredicate_RendersAgainstJobTimelineCards()
    {
        using var db = TestDbContextFactory.Postgres();

        var sql = db.JobTimelineCards.AsNoTracking()
            .Where(AttentionQueryService.CardPredicate([Guid.NewGuid().ToString()]))
            .ToQueryString();

        sql.Should().Contain("FROM job_timeline_cards");
        sql.Should().Contain("job_id");
        sql.Should().Contain("card_type");
        // Status is filtered in memory, not here: the five types do not share one "this is a live wait"
        // status, and a per-type status test in SQL would run on every poll of the endpoint.
        sql.Should().NotContain("status = @");
    }
}
