using AgentMesh.Core.Activities;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Tests.Helpers;
using AgentMesh.Worker.Activities;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMesh.Tests.Activities;

/// <summary>
/// What the stage does when a Stage 1 turn ends having asked nothing.
/// </summary>
/// <remarks>
/// <para>
/// A missing Discovery Q&A card is unfalsifiable from the cockpit: an agent with nothing to ask and an
/// agent that asked in prose nobody could see both leave the same empty result. These tests pin the one
/// mechanism that tells them apart — the stage asks once more, and accepts only the two answers it can
/// check: a structured question, or the sentinel that says "I looked, there is nothing to ask".
/// </para>
/// <para>
/// The other thing they pin is what the stage must never do again: infer a question from prose. A card
/// assembled out of sentences the agent never meant as questions makes the operator answer a question
/// nobody asked, which steers the job with words it did not say.
/// </para>
/// </remarks>
public class DiscoveryNoQuestionsRepromptTests
{
    private const string ProseThatLooksLikeQuestions =
        "Q1. Should the gate reuse Postgres or add Redis? Q2. Do we need backwards compatibility?\n" +
        "The migration is straightforward and I will start with the schema.";

    private const string FencedAskQuestion =
        "Before Stage 1B I need two calls:\n" +
        "```ask_question\n" +
        "{\"questions\":[{\"question\":\"Should the gate reuse the existing Postgres instance?\",\"options\":[\"Yes, Postgres\",\"Add Redis\"]}]}\n" +
        "```";

    private sealed class ScriptedTurnRunner(Func<int, TurnOutcome> script) : IAgentTurnRunner
    {
        public List<AgentTurnRequest> Requests { get; } = [];

        public Task<TurnOutcome> RunTurnAsync(AgentTurnRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(script(Requests.Count));
        }
    }

    private sealed class RecordingPromptStackBuilder : IPromptStackBuilder
    {
        /// <summary>The incoming message each turn was dispatched with, in dispatch order.</summary>
        public List<string?> PendingReplies { get; } = [];

        public Task<AssembledPromptStack> BuildPromptStackAsync(PromptStackContext context, CancellationToken ct = default)
        {
            PendingReplies.Add(context.PendingReply?.Content);
            return Task.FromResult(new AssembledPromptStack(
                CachedSystemPrompt: "cached",
                UserTaskEnvelope: context.UserPrompt,
                History: [],
                WorkspacePath: context.WorkspacePath));
        }
    }

    private static (AgentMeshDbContext Db, Guid JobId, Guid SessionId) Seed(string? metadata = null)
    {
        var db = TestDbContextFactory.Create();
        var jobId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        db.ProjectJobs.Add(new ProjectJob
        {
            Id = jobId,
            ProjectId = "agent-mesh",
            Title = "Plan gate recovery",
            Description = "Give the operator a raw message at the plan gate",
            AssignedAgentId = "rook"
        });

        db.AgentSessions.Add(new AgentSession
        {
            Id = sessionId,
            Title = "stage-1",
            PersonaId = "rook",
            MetadataJson = metadata ?? $"{{\"jobId\":\"{jobId}\"}}",
            UpdatedAt = DateTimeOffset.UtcNow
        });

        db.SaveChanges();
        return (db, jobId, sessionId);
    }

    private static async Task<DiscoveryQaActivityResult> RunAsync(
        AgentMeshDbContext db,
        Guid jobId,
        ScriptedTurnRunner runner,
        RecordingPromptStackBuilder stackBuilder)
    {
        var activities = new TimelineActivities(
            db,
            NullLogger<TimelineActivities>.Instance,
            stackBuilder,
            runner);

        return await activities.ExecuteDiscoveryQaActivityAsync(new DiscoveryQaActivityInput(
            jobId.ToString(),
            "Add operator guidance to the plan gate",
            "/workspace/agent-mesh"));
    }

    [Fact]
    public async Task SilentTurn_FollowUpTurnThatAsks_StructurallyCreatesTheCard()
    {
        var (db, jobId, _) = Seed();
        var runner = new ScriptedTurnRunner(turn => turn == 1
            ? TurnOutcome.ContinueTurn(ProseThatLooksLikeQuestions)
            : TurnOutcome.ContinueTurn(FencedAskQuestion));

        var result = await RunAsync(db, jobId, runner, new RecordingPromptStackBuilder());

        runner.Requests.Should().HaveCount(2, "a turn that asked nothing gets exactly one follow-up");
        result.CardId.Should().NotBeEmpty("the follow-up asked through a sanctioned channel");
        result.Payload.Questions.Should().ContainSingle()
            .Which.Question.Should().Be("Should the gate reuse the existing Postgres instance?");
    }

    [Fact]
    public async Task FollowUp_NamesBothExitsItWillAccept()
    {
        var (db, jobId, _) = Seed();
        var stackBuilder = new RecordingPromptStackBuilder();
        var runner = new ScriptedTurnRunner(_ => TurnOutcome.ContinueTurn("Nothing to add."));

        await RunAsync(db, jobId, runner, stackBuilder);

        stackBuilder.PendingReplies.Should().HaveCount(2);
        stackBuilder.PendingReplies[0].Should().BeNull("the first Stage 1 turn has no incoming message");
        stackBuilder.PendingReplies[1]!
            .Should().Contain(TimelineActivities.NoQuestionsSentinel)
            .And.Contain("ask_question")
            .And.NotContain("```ask_question", "the nudge must not model the block it is asking for");
    }

    [Fact]
    public async Task TurnThatConfirmsTheSentinel_IsNotPromptedAgain()
    {
        var (db, jobId, _) = Seed();
        var runner = new ScriptedTurnRunner(_ =>
            TurnOutcome.ContinueTurn($"Checklist registered via manage_task. {TimelineActivities.NoQuestionsSentinel}"));

        var result = await RunAsync(db, jobId, runner, new RecordingPromptStackBuilder());

        runner.Requests.Should().HaveCount(1, "the sentinel already answered the stage");
        result.CardId.Should().BeEmpty();
        result.Payload.Questions.Should().BeEmpty();
    }

    [Fact]
    public async Task TurnThatAskedThroughTheToolChannel_IsNotPromptedAgain()
    {
        var (db, jobId, sessionId) = Seed();
        db.SessionMessages.Add(new SessionMessage
        {
            SessionId = sessionId,
            SenderAgentId = "rook",
            EventType = "tool_call",
            ToolName = "ask_question",
            ToolArgsJson = "{\"questions\":[{\"question\":\"Which queue?\",\"options\":[\"Temporal\",\"Redis\"]}]}",
            Content = "ask_question",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var runner = new ScriptedTurnRunner(_ => TurnOutcome.ContinueTurn("I have a question."));
        var result = await RunAsync(db, jobId, runner, new RecordingPromptStackBuilder());

        runner.Requests.Should().HaveCount(1, "a question that was actually asked needs no follow-up");
        result.CardId.Should().NotBeEmpty();
        result.Payload.Questions.Should().ContainSingle().Which.Question.Should().Be("Which queue?");
    }

    [Fact]
    public async Task NeitherExitTaken_RepromptsOnceThenProceedsWithoutInventingQuestions()
    {
        var (db, jobId, _) = Seed();
        var runner = new ScriptedTurnRunner(turn => turn == 1
            ? TurnOutcome.ContinueTurn(ProseThatLooksLikeQuestions)
            : TurnOutcome.ContinueTurn("Well, Q1. is worth asking but I already posted the plan."));

        var result = await RunAsync(db, jobId, runner, new RecordingPromptStackBuilder());

        runner.Requests.Should().HaveCount(2, "one follow-up, then stop: a stage needing two nudges has no question");
        result.CardId.Should().BeEmpty("prose is not a question channel, in either turn");
        result.Payload.Questions.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedTurn_IsNotPromptedToAskAgain()
    {
        var (db, jobId, _) = Seed();
        var runner = new ScriptedTurnRunner(_ => new TurnOutcome(TurnOutcomeStatus.Failed, "runner died mid-turn"));

        var result = await RunAsync(db, jobId, runner, new RecordingPromptStackBuilder());

        runner.Requests.Should().HaveCount(1, "a failed turn owes a retry, not a question prompt");
        result.CardId.Should().BeEmpty();
    }

    [Fact]
    public async Task SessionsThatBelongToAnotherJob_AreNotReadForItsQuestions()
    {
        var (db, jobId, _) = Seed(metadata: "{\"jobId\":\"" + Guid.NewGuid() + "\"}");
        var runner = new ScriptedTurnRunner(turn => turn == 1
            ? TurnOutcome.ContinueTurn(ProseThatLooksLikeQuestions)
            : TurnOutcome.ContinueTurn(FencedAskQuestion));

        var result = await RunAsync(db, jobId, runner, new RecordingPromptStackBuilder());

        runner.Requests.Should().HaveCount(2, "the follow-up still runs, because silence is still unexplained");
        result.CardId.Should().NotBeEmpty("the follow-up's own reply is this job's evidence");
    }
}
