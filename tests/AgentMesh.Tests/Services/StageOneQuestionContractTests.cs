using AgentMesh.Core.Models;
using AgentMesh.Core.Repositories;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Repositories;
using AgentMesh.Tests.Helpers;
using AgentMesh.Worker.Activities;
using AgentMesh.Worker.Prompts;
using AgentMesh.Worker.Services;
using FluentAssertions;

namespace AgentMesh.Tests.Services;

/// <summary>
/// What Stage 1 is told about asking, and whether that contract can be satisfied at all.
/// </summary>
/// <remarks>
/// <para>
/// Discovery Q&A used to go missing on real jobs while the agent, in its own transcript, was writing
/// <c>Q1.</c> through <c>Q5.</c> Nobody invented those questions: the layers told the agent to "post
/// your plan and any clarifying questions via post_job_message", and <c>ask_question</c> — the one
/// channel the operator can see and answer — ends the turn. An agent that followed the prompt exactly
/// could not also register a checklist, so the questions went into prose and the gate advanced.
/// </para>
/// <para>
/// These tests pin the repaired contract from both ends: the prompt must name <c>ask_question</c> as the
/// only question channel and offer both exits, and the confirmation the stage asks for must be the same
/// token <see cref="TimelineActivities"/> actually reads. A prompt that drifts from the reader is the
/// bug class that caused this, so neither half is allowed to move alone.
/// </para>
/// </remarks>
public class StageOneQuestionContractTests
{
    private const string ProjectId = "agent-mesh";

    private static PromptStackContext StageOneContext() =>
        new(
            AgentId: "driver-rook",
            ProjectId: ProjectId,
            JobId: Guid.NewGuid().ToString(),
            TaskId: "stage-1-discovery",
            WorkspacePath: "/workspace/repos/agent-mesh",
            TargetBranch: "main",
            UserPrompt: "Add operator guidance to the plan gate",
            PersonaId: "rook");

    private static async Task<AssembledPromptStack> BuildAsync()
    {
        using var db = TestDbContextFactory.Create();
        db.Projects.Add(new Project { Id = ProjectId, Name = "Agent Mesh", RepositoryName = "agent-mesh" });
        await db.SaveChangesAsync();

        return await new PromptStackBuilder(
            db,
            new WorkspaceStateRepository(db),
            new ShellWorkspaceReconciler(shellRunner: (_, _, _) => Task.FromResult(
                new CommandExecutionResult(true, 0, "@@HEADTYPE@@\nbranch\n@@BRANCH@@\nmain\n@@COUNT@@\n0\n@@FILES@@\n@@STASH@@\n@@HEAD@@\nc0ffee1\n@@DONE@@"))),
            null,
            new AgentMemoryRepository(db))
            .BuildPromptStackAsync(StageOneContext());
    }

    [Fact]
    public async Task StageOne_TellsTheAgentAskQuestionIsTheOnlyQuestionChannel()
    {
        var stack = await BuildAsync();
        var all = stack.CachedSystemPrompt + "\n" + stack.UserTaskEnvelope;

        all.Should().Contain("ask_question");
        all.Should().MatchRegex("(?i)only question channel");
    }

    [Fact]
    public async Task StageOne_NeverAsksForQuestionsToTravelInThePlanMessage()
    {
        var stack = await BuildAsync();
        var all = (stack.CachedSystemPrompt + "\n" + stack.UserTaskEnvelope).ReplaceLineEndings("\n");

        // The line that caused the bug: it named a channel the operator cannot answer, in the same
        // breath as the plan, in a stage where asking ends the turn.
        all.Should().NotMatchRegex("(?i)plan\\s+(and|or|&)\\s+(any\\s+)?(clarifying\\s+)?questions",
            "questions posted with a plan are never shown to the operator");
    }

    [Fact]
    public async Task StageOne_OfferBothExitsAndTheTokenThatConfirmsOne()
    {
        var stack = await BuildAsync();
        var all = stack.CachedSystemPrompt + "\n" + stack.UserTaskEnvelope;

        all.Should().Contain("NO_OPEN_QUESTIONS");
        all.Should().MatchRegex("(?i)two exits|exactly one of");
    }

    [Fact]
    public void PlatformInvariants_ConfirmTheSameTokenTheActivityReads()
    {
        PlatformPrompts.PlatformInvariants
            .Should().Contain(TimelineActivities.NoQuestionsSentinel,
                "the prompt and the reader must name one token, not two spellings of it")
            .And.Contain("ask_question")
            .And.NotMatch("(?si)Post your plan and any clarifying questions via `post_job_message[^\\n]*",
                "the lifecycle text may not route questions into a message the operator cannot answer");
    }

    [Fact]
    public void TheRepromptTextDoesNotModelAQuestionItCouldBeScrapedFrom()
    {
        // The nudge names ask_question and quotes the sentinel, which is exactly the vocabulary the
        // extractor reads. If it ever grew a real example block, every nudged turn would "ask" a
        // question the platform wrote, and the operator would answer it.
        TimelineActivities.NoQuestionsRepromptText
            .Should().NotContain("```ask_question")
            .And.NotContain("\"questions\"");
    }
}
