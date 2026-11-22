using System.Text.Json;
using AgentMesh.Api.Controllers;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
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
        var card = new JobTimelineCard { Id = Guid.NewGuid(), JobId = job.Id.ToString(), CardType = TimelineCardType.TaskStep, Title = "Hello" };
        var assignment = new ProjectPersona { ProjectId = "proj-del", PersonaId = "p1" };

        db.Projects.Add(project);
        db.ProjectJobs.Add(job);
        db.JobTimelineCards.Add(card);
        db.ProjectPersonas.Add(assignment);
        await db.SaveChangesAsync();

        var controller = new ProjectsController(db);
        var result = await controller.DeleteProject("proj-del");
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;

        (await db.Projects.FindAsync("proj-del")).Should().BeNull();
        (await db.ProjectJobs.FindAsync(job.Id)).Should().BeNull();
        (await db.JobTimelineCards.FindAsync(card.Id)).Should().BeNull();
        (await db.ProjectPersonas.FirstOrDefaultAsync(pp => pp.ProjectId == "proj-del")).Should().BeNull();
    }

    [Fact]
    public async Task CreateProject_DefaultsRepositoryNameToSlugAndExposesResolvedWorkingDirectory()
    {
        using var db = TestDbContextFactory.Create();
        var controller = new ProjectsController(db);

        var result = await controller.CreateProject(new CreateProjectRequest(
            Name: "Payments API"
        ));

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var dto = created.Value.Should().BeOfType<ProjectSummaryDto>().Subject;

        dto.Id.Should().Be("payments-api");
        dto.RepositoryName.Should().Be("payments-api");
        dto.WorkingDirectory.Should().BeEmpty();
        dto.ResolvedWorkingDirectory.Should().Be($"{WorkspacePathResolver.ReposRoot}/payments-api");

        var saved = await db.Projects.SingleAsync(p => p.Id == "payments-api");
        saved.RepositoryName.Should().Be("payments-api");
        saved.WorkingDirectory.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateProject_WithExplicitRepositoryNameAndWorkingDirectory_NormalizesAndPersists()
    {
        using var db = TestDbContextFactory.Create();
        var controller = new ProjectsController(db);

        var result = await controller.CreateProject(new CreateProjectRequest(
            Name: "Monorepo Service",
            Id: "svc-api",
            RepositoryName: "monorepo",
            WorkingDirectory: "/workspace/repos/monorepo/services/api/"
        ));

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var dto = created.Value.Should().BeOfType<ProjectSummaryDto>().Subject;

        dto.RepositoryName.Should().Be("monorepo");
        dto.WorkingDirectory.Should().Be("/workspace/repos/monorepo/services/api");
        dto.ResolvedWorkingDirectory.Should().Be("/workspace/repos/monorepo/services/api");
    }

    [Theory]
    [InlineData("../../etc")]
    [InlineData("org/repo")]
    [InlineData("repo name")]
    [InlineData("..")]
    public async Task CreateProject_UnsafeRepositoryName_ReturnsBadRequest(string badRepoName)
    {
        using var db = TestDbContextFactory.Create();
        var controller = new ProjectsController(db);

        var result = await controller.CreateProject(new CreateProjectRequest(
            Name: "Bad Repo",
            RepositoryName: badRepoName
        ));

        result.Should().BeOfType<BadRequestObjectResult>();
        (await db.Projects.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("has space")]
    [InlineData("has\nnewline")]
    public async Task CreateProject_UnsafeWorkingDirectory_ReturnsBadRequest(string badWorkingDirectory)
    {
        using var db = TestDbContextFactory.Create();
        var controller = new ProjectsController(db);

        var result = await controller.CreateProject(new CreateProjectRequest(
            Name: "Bad Dir",
            WorkingDirectory: badWorkingDirectory
        ));

        result.Should().BeOfType<BadRequestObjectResult>();
        (await db.Projects.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task GetProject_ExposesRepositoryNameWorkingDirectoryAndResolvedPath()
    {
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(new Project
        {
            Id = "billing",
            Name = "Billing",
            RepositoryName = "billing-core",
            WorkingDirectory = string.Empty
        });
        await db.SaveChangesAsync();

        var controller = new ProjectsController(db);
        var result = await controller.GetProject("billing");

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ProjectSummaryDto>().Subject;

        dto.RepositoryName.Should().Be("billing-core");
        dto.WorkingDirectory.Should().BeEmpty();
        dto.ResolvedWorkingDirectory.Should().Be($"{WorkspacePathResolver.ReposRoot}/billing-core");
    }

    [Fact]
    public async Task UpdateProject_UpdatesAndClearsWorkspaceFields_AndRejectsInvalidValues()
    {
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(new Project
        {
            Id = "proj-ws",
            Name = "Workspace Project",
            RepositoryName = "initial-repo",
            WorkingDirectory = string.Empty
        });
        await db.SaveChangesAsync();

        var controller = new ProjectsController(db);

        // 1. Update both RepositoryName and WorkingDirectory
        var updateResult = await controller.UpdateProject("proj-ws", new UpdateProjectRequest(
            RepositoryName: "updated-repo",
            WorkingDirectory: "/workspace/repos/custom/path/"
        ));
        var ok1 = updateResult.Should().BeOfType<OkObjectResult>().Subject;
        var dto1 = ok1.Value.Should().BeOfType<ProjectSummaryDto>().Subject;
        dto1.RepositoryName.Should().Be("updated-repo");
        dto1.WorkingDirectory.Should().Be("/workspace/repos/custom/path");
        dto1.ResolvedWorkingDirectory.Should().Be("/workspace/repos/custom/path");

        // 2. Clear WorkingDirectory override back to blank (convention)
        var clearResult = await controller.UpdateProject("proj-ws", new UpdateProjectRequest(
            WorkingDirectory: ""
        ));
        var ok2 = clearResult.Should().BeOfType<OkObjectResult>().Subject;
        var dto2 = ok2.Value.Should().BeOfType<ProjectSummaryDto>().Subject;
        dto2.RepositoryName.Should().Be("updated-repo");
        dto2.WorkingDirectory.Should().BeEmpty();
        dto2.ResolvedWorkingDirectory.Should().Be($"{WorkspacePathResolver.ReposRoot}/updated-repo");

        // 3. Reject empty/unsafe RepositoryName and relative WorkingDirectory without mutating state
        (await controller.UpdateProject("proj-ws", new UpdateProjectRequest(RepositoryName: "   ")))
            .Should().BeOfType<BadRequestObjectResult>();
        (await controller.UpdateProject("proj-ws", new UpdateProjectRequest(RepositoryName: "../escape")))
            .Should().BeOfType<BadRequestObjectResult>();
        (await controller.UpdateProject("proj-ws", new UpdateProjectRequest(WorkingDirectory: "relative/dir")))
            .Should().BeOfType<BadRequestObjectResult>();

        var persisted = await db.Projects.SingleAsync(p => p.Id == "proj-ws");
        persisted.RepositoryName.Should().Be("updated-repo");
        persisted.WorkingDirectory.Should().BeEmpty();
    }

    [Fact]
    public async Task SetupProjectWorkspace_MissingProject_ReturnsNotFound()
    {
        using var db = TestDbContextFactory.Create();
        var controller = new ProjectsController(db);

        var result = await controller.SetupProjectWorkspace("does-not-exist");

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task SetupProjectWorkspace_ValidGitProject_CreatesStandardStructureAndIsIdempotent()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"agentmesh-api-setup-{Guid.NewGuid():N}");
        var workspaceDir = Path.Combine(tempRoot, "my-service");
        try
        {
            using var db = TestDbContextFactory.Create();
            db.Projects.Add(new Project
            {
                Id = "my-service",
                Name = "My Service",
                Description = "Service workspace test",
                WorkspaceType = WorkspaceType.Git,
                RepositoryName = "my-service",
                WorkingDirectory = workspaceDir,
                GitBaseBranch = "main"
            });
            await db.SaveChangesAsync();

            var controller = new ProjectsController(db);

            // First call initializes the workspace on disk.
            var firstResult = await controller.SetupProjectWorkspace("my-service");
            var firstOk = firstResult.Should().BeOfType<OkObjectResult>().Subject;
            var firstDto = firstOk.Value.Should().BeOfType<ProjectWorkspaceSetupResponseDto>().Subject;

            firstDto.ProjectId.Should().Be("my-service");
            firstDto.Success.Should().BeTrue();
            firstDto.Outcome.Should().Be(nameof(ProjectWorkspaceSetupOutcome.Initialized));
            firstDto.CreatedFiles.Should().BeTrue();
            firstDto.UsedFallback.Should().BeFalse();
            firstDto.EffectiveDirectory.Should().Be(WorkspacePathResolver.Normalize(workspaceDir));
            firstDto.Error.Should().BeNull();

            Directory.Exists(Path.Combine(workspaceDir, ".git")).Should().BeTrue();
            Directory.Exists(Path.Combine(workspaceDir, "src")).Should().BeTrue();
            Directory.Exists(Path.Combine(workspaceDir, "tests")).Should().BeTrue();
            Directory.Exists(Path.Combine(workspaceDir, "docs")).Should().BeTrue();
            File.Exists(Path.Combine(workspaceDir, "README.md")).Should().BeTrue();
            File.Exists(Path.Combine(workspaceDir, ".gitignore")).Should().BeTrue();

            // Second call is a no-op returning AlreadyInitialized.
            var secondResult = await controller.SetupProjectWorkspace("my-service");
            var secondOk = secondResult.Should().BeOfType<OkObjectResult>().Subject;
            var secondDto = secondOk.Value.Should().BeOfType<ProjectWorkspaceSetupResponseDto>().Subject;

            secondDto.Success.Should().BeTrue();
            secondDto.Outcome.Should().Be(nameof(ProjectWorkspaceSetupOutcome.AlreadyInitialized));
            secondDto.CreatedFiles.Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SetupProjectWorkspace_WhenSetupFails_ReturnsUnprocessableEntityWithFailureDetails()
    {
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(new Project
        {
            Id = "failing-proj",
            Name = "Failing Project",
            RepositoryName = "failing-proj",
            WorkingDirectory = "/workspace/repos/failing-proj"
        });
        await db.SaveChangesAsync();

        var mockSetup = new Mock<IProjectWorkspaceSetup>();
        mockSetup
            .Setup(s => s.EnsureWorkspaceAsync(
                It.IsAny<Project>(),
                "/tmp/fallback",
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProjectWorkspaceSetupResult.Failure(
                "/workspace/repos/failing-proj",
                "Remote clone failed.",
                new[] { "auth required" }));

        var controller = new ProjectsController(db, workspaceSetup: mockSetup.Object);

        var result = await controller.SetupProjectWorkspace("failing-proj", fallbackDirectory: "/tmp/fallback");

        var unprocessable = result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        var dto = unprocessable.Value.Should().BeOfType<ProjectWorkspaceSetupResponseDto>().Subject;

        dto.ProjectId.Should().Be("failing-proj");
        dto.Success.Should().BeFalse();
        dto.Outcome.Should().Be(nameof(ProjectWorkspaceSetupOutcome.Failed));
        dto.Message.Should().Be("Remote clone failed.");
        dto.Error.Should().Be("Remote clone failed.");
        dto.Warnings.Should().ContainSingle("auth required");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // The write boundary for the repository binding.
    //
    // projects.repository_name shipped with a column default of Project.DefaultRepositoryName — this
    // deployment's own repository — so every project created before the create-time fix inherited it,
    // and the dashboard re-sends the unchanged field on every save. The row then resolves every agent
    // command onto another repository's checkout, and the job branches and commits there with no error
    // anywhere. These tests pin the rejection, the repair, and the two legitimate escapes.
    // ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateProject_PlaceholderRepositoryNameOnForeignRemote_IsRejectedWithTheFix()
    {
        using var db = TestDbContextFactory.Create();
        var controller = new ProjectsController(db);

        var result = await controller.CreateProject(new CreateProjectRequest(
            Name: "Budget App",
            Id: "budget-app",
            WorkspaceType: WorkspaceType.Git,
            GitRepositoryUrl: "https://github.com/ckchessmaster/budget-app",
            RepositoryName: "agent-mesh"));

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var error = JsonSerializer.Serialize(bad.Value);
        error.Should().Contain("/workspace/repos/agent-mesh");
        error.Should().Contain("/workspace/repos/budget-app");

        (await db.Projects.AnyAsync(p => p.Id == "budget-app")).Should().BeFalse();
    }

    [Fact]
    public async Task CreateProject_ForeignRemoteWithNoRepositoryName_DerivesItsOwnDirectory()
    {
        using var db = TestDbContextFactory.Create();
        var controller = new ProjectsController(db);

        var result = await controller.CreateProject(new CreateProjectRequest(
            Name: "Budget App",
            Id: "budget-app",
            WorkspaceType: WorkspaceType.Git,
            GitRepositoryUrl: "https://github.com/ckchessmaster/budget-app"));

        var created = result.Should().BeOfType<CreatedAtActionResult>().Subject;
        var dto = created.Value.Should().BeOfType<ProjectSummaryDto>().Subject;

        dto.RepositoryName.Should().Be("budget-app");
        dto.ResolvedWorkingDirectory.Should().Be("/workspace/repos/budget-app");
    }

    [Fact]
    public async Task UpdateProject_UnrelatedEditOnLegacyPlaceholderRow_StatesTheBindingFix()
    {
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(new Project
        {
            Id = "budget-app",
            Name = "Budget App",
            WorkspaceType = WorkspaceType.Git,
            GitRepositoryUrl = "https://github.com/ckchessmaster/budget-app",
            RepositoryName = Project.DefaultRepositoryName,
            WorkingDirectory = string.Empty
        });
        await db.SaveChangesAsync();

        var controller = new ProjectsController(db);

        // The dashboard re-sends every unchanged field, so a name-only edit must not be allowed to
        // quietly re-commit the placeholder.
        var result = await controller.UpdateProject("budget-app", new UpdateProjectRequest(Name: "Budget App v2"));

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        // JsonSerializer escapes apostrophes, so assert the phrase without them.
        JsonSerializer.Serialize(bad.Value)
            .Should().Contain("Set repository_name to")
            .And.Contain("budget-app");

        var persisted = await db.Projects.SingleAsync(p => p.Id == "budget-app");
        persisted.Name.Should().Be("Budget App");
    }

    [Fact]
    public async Task UpdateProject_RepointingRepositoryNameAtItsOwnRemote_ResolvesTheRightDirectory()
    {
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(new Project
        {
            Id = "budget-app",
            Name = "Budget App",
            WorkspaceType = WorkspaceType.Git,
            GitRepositoryUrl = "https://github.com/ckchessmaster/budget-app",
            RepositoryName = Project.DefaultRepositoryName,
            WorkingDirectory = string.Empty
        });
        await db.SaveChangesAsync();

        var controller = new ProjectsController(db);

        var result = await controller.UpdateProject(
            "budget-app",
            new UpdateProjectRequest(RepositoryName: "budget-app"));

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ProjectSummaryDto>().Subject;

        dto.RepositoryName.Should().Be("budget-app");
        dto.ResolvedWorkingDirectory.Should().Be("/workspace/repos/budget-app");
    }

    [Fact]
    public async Task UpdateProject_PlaceholderNameWithExplicitWorkingDirectory_IsAllowed()
    {
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(new Project
        {
            Id = "legacy-place",
            Name = "Legacy Place",
            WorkspaceType = WorkspaceType.Git,
            GitRepositoryUrl = "https://github.com/ckchessmaster/elsewhere",
            RepositoryName = Project.DefaultRepositoryName,
            WorkingDirectory = "/workspace/repos/legacy-place"
        });
        await db.SaveChangesAsync();

        var controller = new ProjectsController(db);

        var result = await controller.UpdateProject("legacy-place", new UpdateProjectRequest(Name: "Still fine"));

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<ProjectSummaryDto>().Subject;

        dto.ResolvedWorkingDirectory.Should().Be("/workspace/repos/legacy-place");
    }

    [Fact]
    public async Task UpdateProject_MonorepoProjectNamingItsCheckoutDifferently_IsAllowed()
    {
        // One remote, several projects: each deliberately names its own checkout. Only the inherited
        // placeholder is a conflict, so this save must keep working.
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(new Project
        {
            Id = "billing-api",
            Name = "Billing API",
            WorkspaceType = WorkspaceType.Git,
            GitRepositoryUrl = "https://github.com/acme/monorepo.git",
            RepositoryName = "billing-api",
            WorkingDirectory = string.Empty
        });
        await db.SaveChangesAsync();

        var controller = new ProjectsController(db);

        var result = await controller.UpdateProject("billing-api", new UpdateProjectRequest(Name: "Billing"));

        result.Should().BeOfType<OkObjectResult>();
    }
}
