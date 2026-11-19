using System.Text.Json;
using AgentMesh.Core.Activities;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Tests.Helpers;
using AgentMesh.Worker.Activities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMesh.Tests.Activities;

/// <summary>
/// What the plan gate reports when Stage 1B registered no checklist.
/// </summary>
/// <remarks>
/// <para>
/// The stage used to fill a gap like that with four hardcoded AgentMesh tasks — "Temporal Orchestration &amp;
/// Activities", "Cockpit Dashboard &amp; Stateful Cards" — whatever the job was actually about. In November
/// 2026 that put a real operator in front of a real approval prompt for a budget app, listing paths from this
/// repository, because the persona's <c>manage_task</c> call had gone to Antigravity's built-in tool of the
/// same name instead of to the platform.
/// </para>
/// <para>
/// An empty plan is now reported as empty. That looks worse than a confident placeholder and is strictly
/// better than acting on one: the operator can see nothing was proposed, and the workflow refuses to run on
/// it. These tests pin the honesty, because the temptation to "help" by filling the gap is permanent.
/// </para>
/// </remarks>
public class DecompositionEmptyChecklistTests
{
    private sealed class ScriptedTurnRunner(string message) : IAgentTurnRunner
    {
        public int Calls { get; private set; }

        public Task<TurnOutcome> RunTurnAsync(AgentTurnRequest request, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(TurnOutcome.ContinueTurn(message));
        }
    }

    private sealed class StubPromptStackBuilder : IPromptStackBuilder
    {
        public Task<AssembledPromptStack> BuildPromptStackAsync(PromptStackContext context, CancellationToken ct = default) =>
            Task.FromResult(new AssembledPromptStack("cached", context.UserPrompt, [], "/workspace/repos/budget-app"));
    }

    private static async Task<(AgentMeshDbContext Db, Guid JobId)> SeedJob(string? tasksJson = "[]")
    {
        var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        db.ProjectJobs.Add(new ProjectJob
        {
            Id = jobId,
            ProjectId = "budget-app",
            Title = "Reallocate a budget line",
            Description = "Move money between categories",
            AssignedAgentId = "astra",
            TasksJson = tasksJson
        });
        await db.SaveChangesAsync();
        return (db, jobId);
    }

    private static async Task<(DecompositionActivityResult Result, JobTimelineCard Card, ProjectJob Job)> RunAsync(
        AgentMeshDbContext db,
        Guid jobId,
        IAgentTurnRunner runner)
    {
        var activities = new TimelineActivities(
            db,
            NullLogger<TimelineActivities>.Instance,
            new StubPromptStackBuilder(),
            runner);

        var result = await activities.ExecuteDecompositionActivityAsync(new DecompositionActivityInput(
            jobId.ToString(),
            "Reallocate a budget line between categories",
            "/workspace/repos/budget-app",
            Feedback: null,
            Answers: new Dictionary<string, string> { ["q1"] = "Categories are fixed" }));

        var card = await db.JobTimelineCards.SingleAsync(c => c.JobId == jobId.ToString());
        var job = await db.ProjectJobs.SingleAsync(j => j.Id == jobId);
        return (result, card, job);
    }

    private static string ReasoningOf(JobTimelineCard card)
    {
        using var doc = JsonDocument.Parse(card.StructuredPayloadJson);
        return doc.RootElement.GetProperty("reasoning").GetString() ?? string.Empty;
    }

    [Fact]
    public async Task DecompositionThatRegisteredNothing_DoesNotInventTasks()
    {
        var (db, jobId) = await SeedJob();
        // The wording a driver produces when it believes it called the platform tool: this is exactly what
        // the agy turn said after it had driven Antigravity's own background-shell manager.
        var runner = new ScriptedTurnRunner("Checklist registered via manage_task. NO_OPEN_QUESTIONS");

        var (result, _, _) = await RunAsync(db, jobId, runner);

        runner.Calls.Should().Be(1, "the stage still asks the persona to plan; it just does not plan for it");
        result.Payload.Tasks.Should().BeEmpty(
            "a task nobody proposed cannot be executed, reviewed, or approved on the operator's behalf");
    }

    [Fact]
    public async Task TheGateSaysOutLoudThatNoChecklistWasRegistered()
    {
        var (db, jobId) = await SeedJob();

        var (_, card, _) = await RunAsync(db, jobId, new ScriptedTurnRunner("I registered the checklist."));

        card.Title.Should().Be("Plan Approval: No Checklist Registered");
        card.Status.Should().Be(TimelineCardStatus.PendingInput,
            "the operator still holds the decision; the platform has only lost the right to pretend there was a plan");

        var reasoning = ReasoningOf(card);
        reasoning.Should().Contain("No checklist was registered")
            .And.Contain("agentmesh task set-checklist", "the reason has to name the route the driver was missing")
            .And.Contain("Nothing was invented");
        reasoning.Should().NotContain("Generated execution decomposition",
            "nothing was generated, and a confident sentence over a gap is exactly the bug");
    }

    [Fact]
    public async Task TheJobCarriesNoTraceOfThePlaceholderBreakdown()
    {
        var (db, jobId) = await SeedJob();

        var (_, _, job) = await RunAsync(db, jobId, new ScriptedTurnRunner("Done."));

        job.ProgressHeartbeat.Should().Contain("no checklist registered");
        (job.TasksJson ?? string.Empty).Should().NotContain("Temporal Orchestration")
            .And.NotContain("Stateful Cards");
    }

    [Fact]
    public async Task AChecklistThatDoesExist_IsStillPresentedNormally()
    {
        var tasks = JsonSerializer.Serialize(new[]
        {
            new
            {
                id = Guid.NewGuid(),
                title = "Add the reallocation service",
                target_file = "internal/server/reallocation.go",
                verification_method = "go test ./..."
            }
        });
        var (db, jobId) = await SeedJob(tasks);
        var runner = new ScriptedTurnRunner("never dispatched");

        var (result, card, _) = await RunAsync(db, jobId, runner);

        result.Payload.Tasks.Should().ContainSingle().Which.Title.Should().Be("Add the reallocation service");
        card.Title.Should().Be("Plan Approval: Proposed Implementation Checklist");
        runner.Calls.Should().Be(0, "an existing checklist does not need another decomposition turn");
    }
}
