using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentMesh.Tests.Data;

public class ProjectModelTests
{
    [Fact]
    public async Task CanPersistAndRetrieve_Project_WithPersonasAndJobs()
    {
        using var db = TestDbContextFactory.Create();

        // 1. Create Driver and Persona
        var driver = new Agent
        {
            Id = "agy",
            Name = "Antigravity Driver",
            RuntimeType = RuntimeType.CliAgy,
            TrustTier = TrustTier.SeniorManager
        };
        var persona = new Persona
        {
            Id = "astra",
            Name = "Astra",
            Role = "Tech Lead",
            DriverId = "agy",
            PersonaPrompt = "You are Astra."
        };
        db.Agents.Add(driver);
        db.Personas.Add(persona);
        await db.SaveChangesAsync();

        // 2. Create Project
        var project = new Project
        {
            Id = "agent-mesh",
            Name = "AgentMesh Core",
            Description = "Multi-agent coordination system",
            WorkspaceType = WorkspaceType.Git,
            GitRepositoryUrl = "https://github.com/example/agent-mesh.git",
            GitBaseBranch = "main",
            ProjectInstructions = "Follow .NET 10 & React conventions.",
            DefaultLeadPersonaId = "astra"
        };
        db.Projects.Add(project);

        // Assign Persona
        var assignment = new ProjectPersona
        {
            ProjectId = "agent-mesh",
            PersonaId = "astra"
        };
        db.ProjectPersonas.Add(assignment);

        // 3. Create Project Job
        var job = new ProjectJob
        {
            Id = Guid.NewGuid(),
            ProjectId = "agent-mesh",
            Title = "Implement OAuth2 Flow",
            Description = "Add Google OAuth provider",
            Status = ProjectJobStatus.Active,
            GitBranchName = "mesh/job-auth-oauth2",
            TasksJson = "[{\"id\":\"" + Guid.NewGuid() + "\",\"title\":\"Scaffold auth\",\"isCompleted\":false}]"
        };
        db.ProjectJobs.Add(job);

        // 4. Create Job Timeline Card + Comment (unbounded prompt/description)
        job.Description = new string('A', 4096) + " - Full unbounded spec";
        var card = new JobTimelineCard
        {
            Id = Guid.NewGuid(),
            JobId = job.Id.ToString(),
            CardType = TimelineCardType.InternalConsultation,
            Status = TimelineCardStatus.Active,
            Title = "Initial OAuth consultation",
            StructuredPayloadJson = "{\"content\":\"@astra please implement the OAuth endpoints\",\"authorPersonaId\":\"rook\",\"targetPersona\":\"astra\"}"
        };
        var comment = new JobCardComment
        {
            Id = Guid.NewGuid(),
            CardId = card.Id,
            SenderId = "operator",
            SenderType = "operator",
            Content = "@astra please implement the OAuth endpoints"
        };
        db.JobTimelineCards.Add(card);
        db.JobCardComments.Add(comment);

        await db.SaveChangesAsync();

        // Verify retrieval
        var savedProject = await db.Projects.FirstOrDefaultAsync(p => p.Id == "agent-mesh");
        savedProject.Should().NotBeNull();
        savedProject!.Name.Should().Be("AgentMesh Core");
        savedProject.WorkspaceType.Should().Be(WorkspaceType.Git);
        savedProject.DefaultLeadPersonaId.Should().Be("astra");

        var assignments = await db.ProjectPersonas.Where(pp => pp.ProjectId == "agent-mesh").ToListAsync();
        assignments.Should().ContainSingle(pp => pp.PersonaId == "astra");

        var savedJob = await db.ProjectJobs.FirstOrDefaultAsync(j => j.ProjectId == "agent-mesh");
        savedJob.Should().NotBeNull();
        savedJob!.Title.Should().Be("Implement OAuth2 Flow");
        savedJob.Description.Length.Should().BeGreaterThan(4096);
        savedJob.GitBranchName.Should().Be("mesh/job-auth-oauth2");

        var savedCards = await db.JobTimelineCards.Include(c => c.Comments).Where(c => c.JobId == job.Id.ToString()).ToListAsync();
        savedCards.Should().ContainSingle();
        savedCards[0].StructuredPayloadJson.Should().Contain("@astra");
        savedCards[0].Comments.Should().ContainSingle(m => m.Content.Contains("@astra"));
    }
}
