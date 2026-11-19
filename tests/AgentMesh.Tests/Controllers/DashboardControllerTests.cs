using System.Reflection;
using System.Text.Json;
using AgentMesh.Api.Controllers;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentMesh.Tests.Controllers;

public class DashboardControllerTests
{
    [Fact]
    public void GetOverview_HasAllowAnonymousAndExpectedRoute()
    {
        var controllerType = typeof(DashboardController);
        controllerType.GetCustomAttribute<RouteAttribute>()?.Template.Should().Be("api/dashboard");
        controllerType.GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull();

        var method = controllerType.GetMethod(nameof(DashboardController.GetOverview));
        method.Should().NotBeNull();
        method!.GetCustomAttribute<AllowAnonymousAttribute>().Should().NotBeNull();
        method.GetCustomAttribute<HttpGetAttribute>()?.Template.Should().Be("overview");
    }

    [Fact]
    public async Task GetOverview_EmptyDatabase_ReturnsZeroStatsAndEmptyLists()
    {
        using var db = TestDbContextFactory.Create();
        var controller = new DashboardController(db);

        var result = await controller.GetOverview();
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<DashboardOverviewDto>().Subject;

        dto.RecentJobs.Should().BeEmpty();
        dto.AttentionItems.Should().BeEmpty();
        dto.JobStats.TotalProjects.Should().Be(0);
        dto.JobStats.ActiveProjects.Should().Be(0);
        dto.JobStats.TotalJobs.Should().Be(0);
        dto.JobStats.ActiveJobs.Should().Be(0);
        dto.JobStats.PendingJobs.Should().Be(0);
        dto.JobStats.CompletedJobs.Should().Be(0);
        dto.JobStats.ArchivedJobs.Should().Be(0);
        dto.JobStats.AttentionCount.Should().Be(0);
        dto.JobStats.ActiveSessions.Should().Be(0);
        dto.JobStats.ThinkingSessions.Should().Be(0);
        dto.JobStats.WaitingForInputSessions.Should().Be(0);
    }

    [Fact]
    public async Task GetOverview_AggregatesCrossProjectJobsStatsAndCardCounts()
    {
        using var db = TestDbContextFactory.Create();
        var now = DateTimeOffset.UtcNow;

        db.Projects.AddRange(
            new Project { Id = "alpha", Name = "Project Alpha" },
            new Project { Id = "beta", Name = "Project Beta" },
            new Project { Id = "idle-proj", Name = "Idle Project" }
        );

        var activeAlpha = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "alpha",
            Title = "Alpha Feature",
            Status = ProjectJobStatus.Active,
            ProgressHeartbeat = "Executing task: Build API",
            AssignedAgentId = "bee",
            CreatedAt = now.AddMinutes(-30),
            UpdatedAt = now.AddMinutes(-2)
        };
        var pendingBeta = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "beta",
            Title = "Beta Backlog",
            Status = ProjectJobStatus.Pending,
            CreatedAt = now.AddMinutes(-20),
            UpdatedAt = now.AddMinutes(-5)
        };
        var completedAlpha = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "alpha",
            Title = "Alpha Hotfix",
            Status = ProjectJobStatus.Completed,
            CreatedAt = now.AddHours(-2),
            UpdatedAt = now.AddMinutes(-15)
        };
        var archivedBeta = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "beta",
            Title = "Beta Old Release",
            Status = ProjectJobStatus.Archived,
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now.AddHours(-6)
        };

        db.ProjectJobs.AddRange(activeAlpha, pendingBeta, completedAlpha, archivedBeta);
        db.JobTimelineCards.AddRange(
            new JobTimelineCard
            {
                Id = Guid.NewGuid(),
                JobId = activeAlpha.Id.ToString(),
                CardType = TimelineCardType.JobInitialized,
                Status = TimelineCardStatus.Active,
                Title = "Initialized"
            },
            new JobTimelineCard
            {
                Id = Guid.NewGuid(),
                JobId = activeAlpha.Id.ToString(),
                CardType = TimelineCardType.TaskStep,
                Status = TimelineCardStatus.Active,
                Title = "Step 1"
            }
        );
        db.AgentSessions.AddRange(
            new AgentSession { Id = Guid.NewGuid(), Title = "Thinking turn", Status = "thinking", PersonaId = "bee" },
            new AgentSession { Id = Guid.NewGuid(), Title = "Active chat", Status = "active", PersonaId = "astra" },
            new AgentSession { Id = Guid.NewGuid(), Title = "Finished turn", Status = "completed", PersonaId = "rook" }
        );
        await db.SaveChangesAsync();

        var controller = new DashboardController(db);

        // Default includeArchived = true
        var result = await controller.GetOverview();
        var dto = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<DashboardOverviewDto>().Subject;

        dto.JobStats.TotalProjects.Should().Be(3);
        dto.JobStats.ActiveProjects.Should().Be(1);
        dto.JobStats.TotalJobs.Should().Be(4);
        dto.JobStats.ActiveJobs.Should().Be(1);
        dto.JobStats.PendingJobs.Should().Be(1);
        dto.JobStats.CompletedJobs.Should().Be(1);
        dto.JobStats.ArchivedJobs.Should().Be(1);
        dto.JobStats.ActiveSessions.Should().Be(2);
        dto.JobStats.ThinkingSessions.Should().Be(1);
        dto.JobStats.WaitingForInputSessions.Should().Be(0);

        dto.RecentJobs.Should().HaveCount(4);
        dto.RecentJobs.Select(j => j.Id).Should().ContainInOrder(
            activeAlpha.Id,
            pendingBeta.Id,
            completedAlpha.Id,
            archivedBeta.Id);
        dto.RecentJobs[0].ProjectName.Should().Be("Project Alpha");
        dto.RecentJobs[0].MessageCount.Should().Be(2);
        dto.RecentJobs[0].LifecycleStatus.Should().Be("InProgress");

        // When includeArchived = false, archived jobs are excluded from RecentJobs while JobStats remain complete
        var noArchivedResult = await controller.GetOverview(recentJobsLimit: 2, includeArchived: false);
        var noArchivedDto = noArchivedResult.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<DashboardOverviewDto>().Subject;
        noArchivedDto.RecentJobs.Should().HaveCount(2);
        noArchivedDto.RecentJobs.Select(j => j.Id).Should().ContainInOrder(activeAlpha.Id, pendingBeta.Id);
        noArchivedDto.JobStats.TotalJobs.Should().Be(4);
        noArchivedDto.JobStats.ArchivedJobs.Should().Be(1);
    }

    [Fact]
    public async Task GetOverview_SurfacesPendingCardsWaitingSessionsAndBlockedJobs_OrderedBySeverityAndRecency()
    {
        using var db = TestDbContextFactory.Create();
        var now = DateTimeOffset.UtcNow;

        db.Projects.AddRange(
            new Project { Id = "mesh", Name = "Agent Mesh" },
            new Project { Id = "ops", Name = "Ops Platform" }
        );
        db.Personas.AddRange(
            new Persona { Id = "bee", Name = "Bee", Role = "Principal Engineer", DriverId = "copilot" },
            new Persona { Id = "astra", Name = "Astra", Role = "Senior Engineer", DriverId = "agy" },
            new Persona { Id = "rook", Name = "Rook", Role = "Engineer", DriverId = "claude" }
        );

        // 1. Job with PlanApproval PendingInput card (warning)
        var planJob = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "mesh",
            Title = "Dashboard Landing Page",
            Status = ProjectJobStatus.Active,
            AssignedAgentId = "bee",
            ProgressHeartbeat = "Awaiting plan approval",
            UpdatedAt = now.AddMinutes(-3)
        };
        var planCard = new JobTimelineCard
        {
            Id = Guid.NewGuid(),
            JobId = planJob.Id.ToString(),
            CardType = TimelineCardType.PlanApproval,
            Status = TimelineCardStatus.PendingInput,
            Title = "Plan Approval: Proposed Implementation Checklist",
            StructuredPayloadJson = "{\"reasoning\":\"5-step dashboard architecture ready for review.\"}",
            CreatedAt = now.AddMinutes(-4),
            UpdatedAt = now.AddMinutes(-3)
        };

        // 2. Job with Escalation3R PendingInput card + corresponding waiting_for_input turn session (critical, deduplicated!)
        var escalatedJob = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "ops",
            Title = "Rotate Prod Keys",
            Status = ProjectJobStatus.Active,
            AssignedAgentId = "astra",
            ProgressHeartbeat = "Awaiting input from @operator",
            UpdatedAt = now.AddMinutes(-10)
        };
        var escalationCard = new JobTimelineCard
        {
            Id = Guid.NewGuid(),
            JobId = escalatedJob.Id.ToString(),
            CardType = TimelineCardType.Escalation3R,
            Status = TimelineCardStatus.PendingInput,
            Title = "Escalation (@astra -> @operator)",
            StructuredPayloadJson = "{\"rawContent\":\"Need vault approval before rotating production keys.\",\"personaId\":\"astra\",\"driverId\":\"agy\"}",
            CreatedAt = now.AddMinutes(-11),
            UpdatedAt = now.AddMinutes(-10)
        };
        var duplicateTurnSession = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Job Turn: Rotate Prod Keys (@Astra)",
            Status = "waiting_for_input",
            PersonaId = "astra",
            DriverId = "agy",
            MetadataJson = JsonSerializer.Serialize(new { jobId = escalatedJob.Id.ToString(), projectId = "ops" }),
            CreatedAt = now.AddMinutes(-11),
            UpdatedAt = now.AddMinutes(-10)
        };

        // 3. Standalone interactive session waiting for input (warning)
        var interactiveSession = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Architecture brainstorm",
            Status = "waiting_for_input",
            PersonaId = "rook",
            DriverId = "claude",
            MetadataJson = "{}",
            CreatedAt = now.AddMinutes(-2),
            UpdatedAt = now.AddMinutes(-1)
        };

        // 4. Active job blocked without an open PendingInput card (critical, newer than escalationCard)
        var blockedJob = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "mesh",
            Title = "Fix Flaky Migration",
            Status = ProjectJobStatus.Active,
            AssignedAgentId = "bee",
            ProgressHeartbeat = "Blocked: migration lock held on staging",
            CreatedAt = now.AddMinutes(-6),
            UpdatedAt = now.AddMinutes(-5)
        };

        // 5. Active job awaiting PR review without a PendingInput card (warning)
        var reviewJob = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "ops",
            Title = "Add Audit Trail",
            Status = ProjectJobStatus.Active,
            AssignedAgentId = "rook",
            ProgressHeartbeat = "Awaiting pull request review (PR #42)",
            CreatedAt = now.AddMinutes(-15),
            UpdatedAt = now.AddMinutes(-8)
        };

        // 6. Archived job with leftover PendingInput card + waiting_for_input session -> MUST BE IGNORED
        var archivedJob = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "mesh",
            Title = "Old Archived Job",
            Status = ProjectJobStatus.Archived,
            UpdatedAt = now.AddHours(-1)
        };
        var staleCard = new JobTimelineCard
        {
            Id = Guid.NewGuid(),
            JobId = archivedJob.Id.ToString(),
            CardType = TimelineCardType.DiscoveryQa,
            Status = TimelineCardStatus.PendingInput,
            Title = "Stale Q&A",
            UpdatedAt = now
        };
        var staleTurnSession = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Job Turn: Old Archived Job",
            Status = "waiting_for_input",
            MetadataJson = JsonSerializer.Serialize(new { jobId = archivedJob.Id.ToString() }),
            UpdatedAt = now
        };

        db.ProjectJobs.AddRange(planJob, escalatedJob, blockedJob, reviewJob, archivedJob);
        db.JobTimelineCards.AddRange(planCard, escalationCard, staleCard);
        db.AgentSessions.AddRange(duplicateTurnSession, interactiveSession, staleTurnSession);
        await db.SaveChangesAsync();

        var controller = new DashboardController(db);
        var result = await controller.GetOverview();
        var dto = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<DashboardOverviewDto>().Subject;

        // 5 actionable items: blockedJob (critical, -5m), escalationCard (critical, -10m),
        // interactiveSession (warning, -1m), planCard (warning, -3m), reviewJob (warning, -8m).
        dto.JobStats.AttentionCount.Should().Be(5);
        dto.AttentionItems.Should().HaveCount(5);

        dto.AttentionItems.Select(a => a.Kind).Should().ContainInOrder(
            "job_blocked",
            "escalation",
            "session_waiting_input",
            "plan_approval",
            "job_awaiting_review"
        );

        var blockedItem = dto.AttentionItems[0];
        blockedItem.Severity.Should().Be("critical");
        blockedItem.JobId.Should().Be(blockedJob.Id);
        blockedItem.ProjectName.Should().Be("Agent Mesh");
        blockedItem.PersonaName.Should().Be("Bee");

        var escalationItem = dto.AttentionItems[1];
        escalationItem.CardId.Should().Be(escalationCard.Id);
        escalationItem.JobId.Should().Be(escalatedJob.Id);
        escalationItem.Summary.Should().Be("Need vault approval before rotating production keys.");
        escalationItem.PersonaName.Should().Be("Astra");
        escalationItem.DriverId.Should().Be("agy");

        var sessionItem = dto.AttentionItems[2];
        sessionItem.SessionId.Should().Be(interactiveSession.Id);
        sessionItem.PersonaName.Should().Be("Rook");
        sessionItem.DriverId.Should().Be("claude");

        var planItem = dto.AttentionItems[3];
        planItem.CardId.Should().Be(planCard.Id);
        planItem.Summary.Should().Be("5-step dashboard architecture ready for review.");

        // Verify attentionLimit bounds returned items while keeping JobStats.AttentionCount accurate
        var limitedRes = await controller.GetOverview(attentionLimit: 2);
        var limitedDto = limitedRes.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<DashboardOverviewDto>().Subject;
        limitedDto.AttentionItems.Should().HaveCount(2);
        limitedDto.JobStats.AttentionCount.Should().Be(5);
    }

    [Fact]
    public async Task GetOverview_IncludesActiveTurnInterruptedCards_UntilResolved()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();

        db.Projects.Add(new Project { Id = "mesh", Name = "Agent Mesh" });
        db.ProjectJobs.Add(new ProjectJob
        {
            Id = jobId,
            ProjectId = "mesh",
            Title = "Long Refactor",
            Status = ProjectJobStatus.Active,
            ProgressHeartbeat = "Turn interrupted: Timeout"
        });
        var interruptedCard = new JobTimelineCard
        {
            Id = Guid.NewGuid(),
            JobId = jobId.ToString(),
            CardType = TimelineCardType.TurnInterrupted,
            Status = TimelineCardStatus.Active,
            Title = "Turn Interrupted: Command timed out",
            StructuredPayloadJson = "{\"reason\":\"Subprocess exceeded 180s timeout\"}"
        };
        db.JobTimelineCards.Add(interruptedCard);
        await db.SaveChangesAsync();

        var controller = new DashboardController(db);
        var dto = (await controller.GetOverview())
            .Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<DashboardOverviewDto>().Subject;

        dto.AttentionItems.Should().ContainSingle();
        dto.AttentionItems[0].Kind.Should().Be("turn_interrupted");
        dto.AttentionItems[0].Severity.Should().Be("critical");
        dto.AttentionItems[0].CardId.Should().Be(interruptedCard.Id);
        dto.AttentionItems[0].Summary.Should().Be("Subprocess exceeded 180s timeout");
    }

    [Fact]
    public void OverviewQueries_TranslateCleanlyToPostgresSql_WithoutArtifactsBlobOrLowercaseStatusScan()
    {
        using var db = TestDbContextFactory.Postgres();

        var recentSql = DashboardController.BuildRecentJobsQuery(db, includeArchived: false, limit: 20).ToQueryString();
        recentSql.Should().Contain("FROM project_jobs");
        recentSql.Should().Contain("ORDER BY");
        recentSql.Should().NotContain("artifacts");

        var liveJobsSql = DashboardController.BuildLiveJobsQuery(db).ToQueryString();
        liveJobsSql.Should().Contain("FROM project_jobs");
        liveJobsSql.Should().NotContain("artifacts");

        var cardsSql = DashboardController.BuildActionableCardsQuery(db).ToQueryString();
        cardsSql.Should().Contain("FROM job_timeline_cards");
        cardsSql.Should().Contain("PendingInput");
        cardsSql.Should().Contain("TurnInterrupted");

        var sessionsSql = DashboardController.BuildLiveSessionsQuery(db).ToQueryString();
        sessionsSql.Should().Contain("FROM agent_sessions");
        sessionsSql.Should().Contain("status");
        sessionsSql.Should().NotContain("LOWER(");
    }
}
