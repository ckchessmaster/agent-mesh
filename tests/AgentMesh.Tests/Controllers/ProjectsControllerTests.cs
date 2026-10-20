using AgentMesh.Api.Controllers;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentMesh.Tests.Controllers;

public class ProjectsControllerTests
{
    [Fact]
    public async Task CreateProject_ValidRequest_CreatesProjectAndAssignsPersonas()
    {
        using var db = TestDbContextFactory.Create();

        // Seed persona
        var persona = new Persona
        {
            Id = "astra",
            Name = "Astra",
            Role = "Tech Lead",
            DriverId = "agy",
            PersonaPrompt = "You are Astra."
        };
        db.Personas.Add(persona);
        await db.SaveChangesAsync();

        var controller = new ProjectsController(db);
        var req = new CreateProjectRequest(
            Name: "Mesh Project",
            Id: "mesh-proj",
            Description: "A collaborative mesh project",
            WorkspaceType: WorkspaceType.Git,
            GitRepositoryUrl: "https://github.com/example/mesh.git",
            GitBaseBranch: "main",
            ProjectInstructions: "Follow clean code principles.",
            DefaultLeadPersonaId: "astra",
            AllowedPersonaIds: new List<string> { "astra" }
        );

        var result = await controller.CreateProject(req);
        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var dto = created.Value.Should().BeOfType<ProjectSummaryDto>().Subject;

        dto.Id.Should().Be("mesh-proj");
        dto.Name.Should().Be("Mesh Project");
        dto.WorkspaceType.Should().Be(WorkspaceType.Git);
        dto.DefaultLeadPersonaId.Should().Be("astra");
        dto.AssignedPersonas.Should().ContainSingle(p => p.Id == "astra");

        var inDb = await db.Projects.FirstOrDefaultAsync(p => p.Id == "mesh-proj");
        inDb.Should().NotBeNull();
        inDb!.DefaultLeadPersonaId.Should().Be("astra");

        var assignment = await db.ProjectPersonas.FirstOrDefaultAsync(pp => pp.ProjectId == "mesh-proj");
        assignment.Should().NotBeNull();
        assignment!.PersonaId.Should().Be("astra");
    }

    [Fact]
    public async Task GetProjects_ReturnsProjectsWithAssignedPersonasAndJobStats()
    {
        using var db = TestDbContextFactory.Create();

        var persona = new Persona { Id = "lead", Name = "Lead", Role = "Architect", DriverId = "agy" };
        var project = new Project
        {
            Id = "proj-1",
            Name = "Project 1",
            WorkspaceType = WorkspaceType.S3Archive,
            DefaultLeadPersonaId = "lead"
        };
        db.Personas.Add(persona);
        db.Projects.Add(project);
        db.ProjectPersonas.Add(new ProjectPersona { ProjectId = "proj-1", PersonaId = "lead" });

        // Add 2 jobs (1 active, 1 completed)
        db.ProjectJobs.Add(new ProjectJob { Id = Guid.NewGuid(), ProjectId = "proj-1", Title = "Job 1", Status = ProjectJobStatus.Active });
        db.ProjectJobs.Add(new ProjectJob { Id = Guid.NewGuid(), ProjectId = "proj-1", Title = "Job 2", Status = ProjectJobStatus.Completed });

        await db.SaveChangesAsync();

        var controller = new ProjectsController(db);
        var result = await controller.GetProjects();
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var list = ok.Value.Should().BeAssignableTo<IEnumerable<ProjectSummaryDto>>().Subject.ToList();

        list.Should().ContainSingle();
        var p = list[0];
        p.Id.Should().Be("proj-1");
        p.DefaultLeadPersonaId.Should().Be("lead");
        p.AssignedPersonas.Should().ContainSingle(per => per.Id == "lead");
        p.JobCount.Should().Be(2);
        p.ActiveJobCount.Should().Be(1);
    }

    [Fact]
    public async Task UpdateProject_UpdatesFieldsAndReassignsPersonas()
    {
        using var db = TestDbContextFactory.Create();

        var p1 = new Persona { Id = "p1", Name = "Persona 1", DriverId = "agy" };
        var p2 = new Persona { Id = "p2", Name = "Persona 2", DriverId = "agy" };
        var project = new Project { Id = "proj-up", Name = "Initial Name", WorkspaceType = WorkspaceType.Git };
        db.Personas.AddRange(p1, p2);
        db.Projects.Add(project);
        db.ProjectPersonas.Add(new ProjectPersona { ProjectId = "proj-up", PersonaId = "p1" });
        await db.SaveChangesAsync();

        var controller = new ProjectsController(db);
        var updateReq = new UpdateProjectRequest(
            Name: "Updated Name",
            WorkspaceType: WorkspaceType.S3Archive,
            AllowedPersonaIds: new List<string> { "p2" }
        );

        var result = await controller.UpdateProject("proj-up", updateReq);
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ProjectSummaryDto>().Subject;

        dto.Name.Should().Be("Updated Name");
        dto.WorkspaceType.Should().Be(WorkspaceType.S3Archive);
        dto.AssignedPersonas.Should().ContainSingle(p => p.Id == "p2");

        var assignments = await db.ProjectPersonas.Where(pp => pp.ProjectId == "proj-up").ToListAsync();
        assignments.Should().ContainSingle(pp => pp.PersonaId == "p2");
    }

    [Fact]
    public async Task DeleteProject_CascadesDeletesJobsAndMessages()
    {
        using var db = TestDbContextFactory.Create();

        var project = new Project { Id = "proj-del", Name = "To Delete" };
        var job = new ProjectJob { Id = Guid.NewGuid(), ProjectId = "proj-del", Title = "Job" };
        var message = new JobMessage { Id = Guid.NewGuid(), JobId = job.Id, Content = "Hello" };
        var assignment = new ProjectPersona { ProjectId = "proj-del", PersonaId = "p1" };

        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        db.JobMessages.Add(message);
        db.ProjectPersonas.Add(assignment);
        await db.SaveChangesAsync();

        var controller = new ProjectsController(db);
        var result = await controller.DeleteProject("proj-del");
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;

        (await db.Projects.FindAsync("proj-del")).Should().BeNull();
        (await db.ProjectJobs.FindAsync(job.Id)).Should().BeNull();
        (await db.JobMessages.FindAsync(message.Id)).Should().BeNull();
        (await db.ProjectPersonas.FirstOrDefaultAsync(pp => pp.ProjectId == "proj-del")).Should().BeNull();
    }
}
