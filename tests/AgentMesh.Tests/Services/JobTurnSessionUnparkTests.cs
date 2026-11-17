using System.Text.Json;
using AgentMesh.Api.Controllers;
using AgentMesh.Api.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Workflows;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Services;

public class JobTurnSessionUnparkTests
{
    [Fact]
    public async Task DispatchAsync_UnparksStaleWaitingForInputSessionsForSameJob()
    {
        using var db = TestDbContextFactory.Create();
        var projectId = Guid.NewGuid().ToString();
        var jobId = Guid.NewGuid();
        var otherJobId = Guid.NewGuid();

        var driver = new Agent
        {
            Id = "agy-driver",
            Name = "Agy",
            Status = "online"
        };
        var persona = new Persona
        {
            Id = "astra",
            Name = "Astra",
            Role = "Tech Lead",
            DriverId = driver.Id,
            PersonaPrompt = "Lead"
        };
        var project = new Project
        {
            Id = projectId,
            Name = "Test Project",
            WorkspaceType = WorkspaceType.Git,
            GitBaseBranch = "main"
        };
        var job = new ProjectJob
        {
            Id = jobId,
            ProjectId = projectId,
            Title = "Standardized project setup",
            GitBranchName = "mesh/job-1"
        };

        var oldJobSession = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Old parked session",
            Status = "waiting_for_input",
            DriverId = driver.Id,
            PersonaId = persona.Id,
            MetadataJson = JsonSerializer.Serialize(new { jobId = jobId.ToString() })
        };
        var otherJobSession = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Other job parked session",
            Status = "waiting_for_input",
            DriverId = driver.Id,
            PersonaId = persona.Id,
            MetadataJson = JsonSerializer.Serialize(new { jobId = otherJobId.ToString() })
        };

        db.Agents.Add(driver);
        db.Personas.Add(persona);
        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        db.AgentSessions.AddRange(oldJobSession, otherJobSession);
        await db.SaveChangesAsync();

        var dispatcher = new JobTurnDispatcher(db);
        var result = await dispatcher.DispatchAsync(new JobTurnRequest(
            job,
            project,
            persona,
            "Implement step 1",
            "operator"));

        result.Session.Id.Should().NotBe(oldJobSession.Id);
        result.Session.Status.Should().Be("thinking");

        var refreshedOld = await db.AgentSessions.FindAsync(oldJobSession.Id);
        refreshedOld!.Status.Should().Be("active");

        var refreshedOther = await db.AgentSessions.FindAsync(otherJobSession.Id);
        refreshedOther!.Status.Should().Be("waiting_for_input");
    }

    [Fact]
    public async Task SubmitPlanGuidance_UnparksWaitingForInputSessionForJob()
    {
        using var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        var cardId = Guid.NewGuid();

        db.JobTimelineCards.Add(new JobTimelineCard
        {
            Id = cardId,
            JobId = jobId.ToString(),
            CardType = TimelineCardType.PlanApproval,
            Status = TimelineCardStatus.PendingInput,
            Title = "Plan Approval",
            StructuredPayloadJson = "{\"tasks\":[],\"reasoning\":\"r\"}"
        });

        var parkedSession = new AgentSession
        {
            Id = Guid.NewGuid(),
            Title = "Discovery session",
            Status = "waiting_for_input",
            MetadataJson = JsonSerializer.Serialize(new { jobId = jobId.ToString() })
        };
        db.AgentSessions.Add(parkedSession);
        await db.SaveChangesAsync();

        var router = new Mock<IWorkflowSignalRouter>(MockBehavior.Strict);
        router.Setup(r => r.SendPlanGuidanceAsync(jobId, It.IsAny<PlanGuidanceSignal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SignalDelivery(jobId.ToString(), SignalDeliveryStatus.Delivered, null));

        var controller = new JobTimelineController(db, null!, hubContext: null, workflows: router.Object);
        var response = await controller.SubmitPlanGuidance(
            jobId.ToString(),
            cardId,
            new PlanGuidanceRequest("Proceed with the plan"),
            CancellationToken.None);

        response.Should().BeOfType<AcceptedResult>();

        var refreshed = await db.AgentSessions.FindAsync(parkedSession.Id);
        refreshed!.Status.Should().Be("active");
    }
}
