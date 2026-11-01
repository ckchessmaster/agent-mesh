using System.Text.Json;
using AgentMesh.Api.Controllers;
using AgentMesh.Api.Hubs;
using AgentMesh.Api.Services;
using AgentMesh.Core.Models;
using AgentMesh.Core.Repositories;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Repositories;
using AgentMesh.Tests.Helpers;
using AgentMesh.Worker.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Services;

/// <summary>
/// The prompt stack travelling the whole dispatch path: composer → worker payload → API → SignalR
/// broadcast → the shape a runner receives.
/// </summary>
/// <remarks>
/// <para>
/// The unit tests in <see cref="PromptStackLayeringTests"/> and
/// <see cref="AgentMesh.Tests.Cli.OpenAiAdapterMessageOrderingTests"/> each prove their own half. This
/// file proves the seam between them, which is where the feature actually dies in practice: a field
/// named <c>promptStack</c> on one side and <c>PromptStack</c> on the other, a camelCase policy applied
/// to the request body but not the broadcast, a record positional constructor with one argument
/// transposed. Every one of those compiles, passes its own unit tests, and ships a turn with no task
/// envelope.
/// </para>
/// <para>
/// The assertions are made against the object the hub actually broadcast, serialised and deserialised
/// the way the wire does it, rather than against the in-memory request. A contract that only holds while
/// both sides share a reference is not a contract.
/// </para>
/// </remarks>
public class PromptStackDispatchIntegrationTests
{
    private const string ProjectId = "agent-mesh";
    private static readonly Guid JobId = Guid.Parse("dddddddd-1111-1111-1111-111111111111");
    private static readonly Guid TaskId = Guid.Parse("eeeeeeee-2222-2222-2222-222222222222");

    private readonly string _dbName = "PromptDispatch_" + Guid.NewGuid();

    private AgentMeshDbContext Db() => TestDbContextFactory.Create(_dbName);

    // ──────────────────────────────────────────────────────────────────────────
    // Turn 0
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starting a fresh task session injects the full 6-layer envelope on Turn 0.
    /// </summary>
    /// <remarks>
    /// The first clause of the Definition of Done, asserted on the broadcast payload. Each layer is
    /// checked for something only that layer produces, so the test fails if a layer is missing even when
    /// a later one happens to mention the same word.
    /// </remarks>
    [Fact]
    public async Task TurnZero_BroadcastCarriesAllSixLayers()
    {
        using var db = Db();
        await SeedAsync(db);

        var recorder = await DispatchAsync(db, turnSessionId: null);
        var wire = recorder.Read();

        // Layers 1–5, in the cached prefix.
        var prefix = wire.PromptStack!.CachedSystemPrompt;
        prefix.Should().Contain("[LAYER 1: PLATFORM INVARIANTS]")
            .And.Contain("[LAYER 2: MASTER PERSONA]")
            .And.Contain("[LAYER 3: PROJECT MANIFEST]")
            .And.Contain("[LAYER 4: TEAM TOPOLOGY & ESCALATION PATHS]")
            .And.Contain("[LAYER 5: PERSISTENT MEMORY & NOTES");

        // Each layer's own evidence, not just its heading.
        prefix.Should().Contain("Self-merging your own pull request is forbidden", "Layer 1 content")
            .And.Contain("Reproduce before fixing", "Layer 2 content, from the persona row")
            .And.Contain("/workspace/repos/agent-mesh", "Layer 3 content, the resolved working root")
            .And.Contain("Principal Architect: Sorren", "Layer 4 content, from the roster")
            .And.Contain("[environment/dotnet_test_flags]: dotnet test -v quiet --nologo", "Layer 5 content");

        // Layer 6, as the user turn.
        var envelope = wire.PromptStack.UserTaskEnvelope;
        envelope.Should().StartWith(PromptStackWirePayload.EnvelopeMarker)
            .And.Contain("Add the wire contract", "the task title")
            .And.Contain("messages[0] is system", "the acceptance criteria")
            .And.Contain("feat/layers", "the branch")
            .And.Contain("[OBJECTIVE]");

        // The prefix and the envelope are separate fields, which is the entire point of the split.
        prefix.Should().NotContain(PromptStackWirePayload.EnvelopeMarker);
        envelope.Should().NotContain("[LAYER 1: PLATFORM INVARIANTS]");
    }

    /// <summary>
    /// The dispatcher does not prepend its own envelope on top of the composed one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The double-wrap regression, and the one a "does the prompt mention the project?" test cannot see.
    /// The API's legacy path builds <c>[Project Context…] [Job Context…]</c> ahead of the prompt; the
    /// composed stack already carries the project, the branch, the task and the working root. Stacking
    /// them states the same facts twice in two formats, and the two copies drift — an agent can quote a
    /// branch name that is not the one its tools will use.
    /// </para>
    /// <para>
    /// It also breaks the cache: the legacy envelope leads with the volatile branch name, so anything
    /// appended after it can never be a stable prefix.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Dispatch_WithAStack_DoesNotPrependTheLegacyEnvelope()
    {
        using var db = Db();
        await SeedAsync(db);

        var recorder = await DispatchAsync(db, turnSessionId: null);
        var wire = recorder.Read();

        // The flattened prompt is what a pre-split binary reads, so it must still be complete…
        wire.Prompt.Should().Contain("Add the wire contract");

        // …but it must not be wrapped in the API's own envelope.
        wire.Prompt.Should().NotContain("[Project Context:")
            .And.NotContain("[Job Context:",
                "the composed stack already carries these facts, and a second copy is a second thing " +
                "to drift out of agreement with the first");
    }

    /// <summary>
    /// A dispatch with no stack keeps the legacy envelope untouched.
    /// </summary>
    /// <remarks>
    /// The other half of the compatibility contract. Job chat, the queued-turn drain and autonomous
    /// handoff all dispatch without a composer, and they must behave exactly as they did before this
    /// change — otherwise the additive field is a breaking change for three callers that never asked for
    /// it.
    /// </remarks>
    [Fact]
    public async Task Dispatch_WithoutAStack_KeepsTheLegacyEnvelope()
    {
        using var db = Db();
        await SeedAsync(db);

        var recorder = await DispatchAsync(db, turnSessionId: null, withStack: false);
        var wire = recorder.Read();

        wire.PromptStack.Should().BeNull("no composer ran on this path");
        wire.Prompt.Should().Contain("[Project Context:")
            .And.Contain("[Job Context:")
            .And.Contain("Add the wire contract.",
                "the caller's prompt still rides at the end of the legacy envelope");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Resume / pod restart
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A resumed session rehydrates the task envelope and the git recovery note.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The second clause of the Definition of Done, and the scenario the transcript analysis found: a
    /// pod restarts, the harness re-dispatches, and the agent — handed a prompt with no task in it —
    /// announces it is waiting for the user's question. The envelope and the note are what make turn N
    /// indistinguishable, from the agent's point of view, from a turn that never lost its process.
    /// </para>
    /// <para>
    /// The dispatch is modelled the way the runner actually resumes. A <em>live</em> prior session is
    /// re-attached to and never re-dispatched, so the rehydrate case is the terminal one: the runner
    /// starts a brand-new session and the composer finds the previous transcript through the
    /// checkpoint's <c>TurnSessionId</c>, not through the workflow input. Passing the prior id on the
    /// request would instead exercise the controller's re-attach guard, which returns without
    /// broadcasting anything.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Resume_RehydratesTheEnvelopeAndTheRecoveryNote()
    {
        using var db = Db();
        await SeedAsync(db, withCheckpoint: true);

        // A completed prior session holding the earlier work, with the checkpoint pointing at it.
        var priorSession = Guid.NewGuid();
        await SeedSessionAsync(db, priorSession, "the reconciler is wired and the tests are green");
        await MoveCheckpointToSessionAsync(db, priorSession);

        // turnSessionId: null — the fresh dispatch a resumed turn becomes.
        var recorder = await DispatchAsync(db, turnSessionId: null);
        var wire = recorder.Read();

        // The window advanced to the checkpoint's session rather than coming up empty, which is the
        // resume half of the pairing this file is named for.
        wire.PromptStack!.History!.Should().Contain(m => m.Content.Contains("tests are green"),
            "the checkpoint is what tells a resumed turn which session to read");

        var envelope = wire.PromptStack!.UserTaskEnvelope;

        // The task is still named, so the agent does not conclude the session just started.
        envelope.Should().Contain(PromptStackWirePayload.EnvelopeMarker)
            .And.Contain("Add the wire contract")
            .And.Contain("[OBJECTIVE]");

        // The git facts, so it does not spend its first five calls on `git status`.
        envelope.Should().Contain(EnvironmentRecoveryNote.Header)
            .And.Contain("feat/layers");

        // The checkpoint, so "what was I doing" survives.
        envelope.Should().Contain("[WORKING CHECKPOINT")
            .And.Contain("checkpointed the reconciler");

        // The prior turn travels as a structured message, not as prose inside the envelope.
        // The idle-loop guard: the last thing the runner will place in the array is the envelope, and
        // the envelope ends on an imperative objective rather than a status report.
        envelope.Should().Contain("Do not reply that you are waiting for instructions");
    }

    /// <summary>
    /// A resumed session is told it is a continuation.
    /// </summary>
    /// <remarks>
    /// An agent that sees no history section cannot distinguish "nothing happened before" from "the
    /// harness dropped it", and the second reading is the one that makes it re-run work already done.
    /// </remarks>
    [Fact]
    public async Task Resume_StatesThatTheSessionIsContinuing()
    {
        using var db = Db();
        await SeedAsync(db, withCheckpoint: true);

        var priorSession = Guid.NewGuid();
        await SeedSessionAsync(db, priorSession, "earlier work");
        await MoveCheckpointToSessionAsync(db, priorSession);

        var recorder = await DispatchAsync(db, turnSessionId: null);
        var envelope = recorder.Read().PromptStack!.UserTaskEnvelope;

        envelope.Should().Contain("This turn continues the session")
            .And.Contain("read them before repeating any command they show already ran");
    }

    /// <summary>
    /// A first turn says there is no prior conversation rather than being silent about it.
    /// </summary>
    [Fact]
    public async Task FirstTurn_SaysThereIsNoPriorConversation()
    {
        using var db = Db();
        await SeedAsync(db);

        var recorder = await DispatchAsync(db, turnSessionId: null);
        var wire = recorder.Read();

        wire.PromptStack!.UserTaskEnvelope.Should().Contain("No prior messages in this session");
        wire.PromptStack.History.Should().BeNullOrEmpty();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // The wire contract
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The stack survives the full worker → API → SignalR chain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three hops, two of them serialised. The worker POSTs camelCase JSON to the API, the API binds it
    /// into <see cref="StartMacroTurnRequest"/>, and the dispatcher re-emits it inside an anonymous
    /// object over SignalR, where the CLI deserialises it into
    /// <c>PromptReceivedEvent.PromptStack</c>.
    /// </para>
    /// <para>
    /// A casing or naming mismatch anywhere in that chain does not throw. It arrives as a null stack,
    /// the runner takes its documented fallback, and the feature is silently absent in production while
    /// every unit test stays green. This is the only test that would notice.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WireContract_SurvivesTheWorkerToApiToHubChain()
    {
        using var db = Db();
        await SeedAsync(db);

        var stack = await new PromptStackBuilder(
            db,
            new WorkspaceStateRepository(db),
            new ShellWorkspaceReconciler(shellRunner: (_, _, _) => Task.FromResult(CleanProbe())),
            null,
            new AgentMemoryRepository(db))
        .BuildPromptStackAsync(Context());

        var wirePayload = PromptStackWirePayload.From(stack);
        wirePayload.Should().NotBeNull();

        // Hop 1: the worker's POST body, camelCase.
        var postBody = JsonSerializer.Serialize(new
        {
            prompt = stack.FlattenToSinglePrompt(),
            personaId = "rook",
            taskId = TaskId.ToString(),
            promptStack = wirePayload,
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        postBody.Should().Contain("\"promptStack\":")
            .And.Contain("\"cachedSystemPrompt\":")
            .And.Contain("\"userTaskEnvelope\":")
            .And.Contain("\"history\":");

        // Hop 2: the API's model binding.
        var bound = JsonSerializer.Deserialize<StartMacroTurnRequest>(postBody, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        bound!.PromptStack.Should().NotBeNull("a casing mismatch here silently disables the feature");
        bound.PromptStack!.CachedSystemPrompt.Should().Be(stack.CachedSystemPrompt);
        bound.PromptStack.UserTaskEnvelope.Should().Be(stack.UserTaskEnvelope);

        // Hop 3: the SignalR broadcast, asserted byte-for-byte on the prefix.
        var recorder = new HubRecorder();
        var turns = new JobTurnDispatcher(db, recorder.Hub.Object, workspaces: null);
        await turns.DispatchAsync(new JobTurnRequest(
            await db.ProjectJobs.FirstAsync(j => j.Id == JobId),
            await db.Projects.FirstAsync(p => p.Id == ProjectId),
            await db.Personas.FirstAsync(p => p.Id == "rook"),
            bound.Prompt,
            Sender: "temporal-worker",
            PromptStack: bound.PromptStack));

        var wire = recorder.Read();

        wire.PromptStack!.CachedSystemPrompt.Should().Be(stack.CachedSystemPrompt,
            "the prefix is a cache key; a single altered byte through the wire makes every turn a miss");
        wire.PromptStack.UserTaskEnvelope.Should().Be(stack.UserTaskEnvelope);
    }

    /// <summary>
    /// The controller forwards the stack rather than dropping it on the floor.
    /// </summary>
    /// <remarks>
    /// Worth its own test because the failure mode is invisible: the endpoint returns 200 OK, the turn
    /// runs, and the agent simply never receives the layers. Only the payload shows it.
    /// </remarks>
    [Fact]
    public async Task Controller_ForwardsTheStackToTheDispatcher()
    {
        using var db = Db();
        await SeedAsync(db);

        var recorder = await DispatchAsync(db, turnSessionId: null);
        var wire = recorder.Read();

        wire.PromptStack.Should().NotBeNull(
            "a 200 OK with a dropped stack is the same outcome as never building the feature");
    }

    /// <summary>
    /// A pre-split caller posting without the new field is unaffected.
    /// </summary>
    [Fact]
    public async Task Controller_AcceptsAPayloadWithoutTheNewField()
    {
        using var db = Db();
        await SeedAsync(db);

        var recorder = new HubRecorder();
        var turns = new JobTurnDispatcher(db, recorder.Hub.Object, workspaces: null);
        var controller = new MacroTurnController(db, turns);

        // Exactly the body an older worker sends: no `promptStack` key at all.
        var legacyBody = JsonSerializer.Serialize(new
        {
            prompt = "legacy turn",
            personaId = "rook",
            taskId = TaskId.ToString(),
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        var req = JsonSerializer.Deserialize<StartMacroTurnRequest>(legacyBody, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        })!;

        req.PromptStack.Should().BeNull();

        var result = await controller.StartMacroTurn(JobId, req, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        // The legacy caller still gets a broadcast, with the envelope the API builds itself.
        var wire = recorder.Read();
        wire.PromptStack.Should().BeNull();
        wire.Prompt.Should().Contain("[Project Context:").And.Contain("legacy turn");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>What the CLI deserialises out of a <c>PromptReceived</c> broadcast.</summary>
    private sealed record WirePromptEvent(
        string Prompt,
        PromptStackWirePayload? PromptStack,
        string? PersonaPrompt);

    /// <summary>
    /// A hub mock that records the first <c>PromptReceived</c> payload it is asked to broadcast.
    /// </summary>
    /// <remarks>
    /// The recorder has to be installed before the dispatch runs, so the capture is a field on this
    /// small holder rather than a method called afterwards.
    /// </remarks>
    private sealed class HubRecorder
    {
        public Mock<IHubContext<AgentMeshHub>> Hub { get; } = new();

        public object?[]? Captured { get; private set; }

        public HubRecorder()
        {
            var clients = new Mock<IHubClients>();
            var proxy = new Mock<IClientProxy>();

            proxy.Setup(p => p.SendCoreAsync(
                    It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                .Callback<string, object?[], CancellationToken>((name, args, _) =>
                {
                    if (name == "PromptReceived" && Captured is null) Captured = args;
                })
                .Returns(Task.CompletedTask);

            clients.Setup(c => c.All).Returns(proxy.Object);
            clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
            Hub.Setup(h => h.Clients).Returns(clients.Object);
        }

        /// <summary>
        /// The captured payload, round-tripped through JSON the way the wire does it.
        /// </summary>
        /// <remarks>
        /// The dispatcher broadcasts an anonymous object, so what
        /// <see cref="IClientProxy.SendCoreAsync"/> receives is an in-memory instance whose property
        /// names are the only contract with the CLI's reader. Serialising and re-reading is what makes
        /// this a wire test: an anonymous type is exactly where a property-name typo hides, because
        /// nothing binds it to the reader at compile time.
        /// </remarks>
        public WirePromptEvent Read()
        {
            Captured.Should().NotBeNull("the dispatcher must broadcast PromptReceived");

            var json = JsonSerializer.Serialize(Captured![0], JobTurnDispatcher.JsonOpts);
            var back = JsonSerializer.Deserialize<WirePromptEvent>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            })!;

            back.Prompt.Should().NotBeNullOrWhiteSpace();
            return back;
        }
    }

    /// <summary>
    /// Composes the stack, then runs it through the real controller and dispatcher.
    /// </summary>
    private static async Task<HubRecorder> DispatchAsync(
        AgentMeshDbContext db,
        Guid? turnSessionId,
        bool withStack = true)
    {
        var recorder = new HubRecorder();
        var turns = new JobTurnDispatcher(db, recorder.Hub.Object, workspaces: null);
        var controller = new MacroTurnController(db, turns);

        PromptStackWirePayload? stack = null;
        var prompt = "Add the wire contract.";

        if (withStack)
        {
            var assembled = await new PromptStackBuilder(
                db,
                new WorkspaceStateRepository(db),
                new ShellWorkspaceReconciler(shellRunner: (_, _, _) => Task.FromResult(CleanProbe())),
                null,
                new AgentMemoryRepository(db))
            .BuildPromptStackAsync(Context(turnSessionId));

            // The worker sends both shapes: the flattened prompt for a pre-split reader, the layered
            // stack for a runner that can split the prefix from the envelope.
            prompt = assembled.FlattenToSinglePrompt();
            stack = PromptStackWirePayload.From(assembled);
        }

        var req = new StartMacroTurnRequest(
            Prompt: prompt,
            PersonaId: "rook",
            TaskId: TaskId.ToString(),
            TurnSessionId: turnSessionId,
            Sender: "temporal-worker",
            PromptStack: stack);

        var result = await controller.StartMacroTurn(JobId, req, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();

        return recorder;
    }

    private static PromptStackContext Context(Guid? turnSessionId = null) =>
        new(
            AgentId: "driver-rook",
            ProjectId: ProjectId,
            JobId: JobId.ToString(),
            TaskId: TaskId.ToString(),
            WorkspacePath: "/workspace/repos/agent-mesh",
            TargetBranch: "main",
            UserPrompt: "",
            PersonaId: "rook",
            TurnSessionId: turnSessionId?.ToString());

    private static CommandExecutionResult CleanProbe() =>
        new(true, 0, """
            @@HEADTYPE@@
            branch
            @@BRANCH@@
            feat/layers
            @@UPSTREAM@@
            origin/feat/layers
            @@COUNT@@
            2
            @@FILES@@
            M src/a.cs
            M src/b.cs
            @@STASH@@
            @@HEAD@@
            c0ffee1 wire: prompt stack
            @@DONE@@
            """);

    private static async Task SeedAsync(AgentMeshDbContext db, bool withCheckpoint = false)
    {
        db.Projects.Add(new Project
        {
            Id = ProjectId,
            Name = "Agent Mesh",
            RepositoryName = "agent-mesh",
            GitBaseBranch = "main",
            ProjectInstructions = "Reproduce first, verify always.",
        });

        var tasks = new[]
        {
            new JobTaskDto
            {
                Id = TaskId,
                Title = "Add the wire contract",
                Status = JobTaskStatus.InProgress,
                AcceptanceCriteria = "messages[0] is system and messages[1] is user on every dispatch.",
            },
        };

        db.ProjectJobs.Add(new ProjectJob
        {
            Id = JobId,
            ProjectId = ProjectId,
            Title = "Prompt stack",
            Description = "Cache-friendly layering.",
            GitBranchName = "feat/layers",
            TasksJson = JsonSerializer.Serialize(tasks, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }),
        });

        db.Personas.AddRange(
            new Persona { Id = "rook", Name = "Rook", Role = "Software Engineer", PersonaPrompt = "Reproduce before fixing.", DriverId = "driver-rook" },
            new Persona { Id = "sorren", Name = "Sorren", Role = "Principal Architect", DriverId = "driver-sorren" },
            new Persona { Id = "astra", Name = "Astra", Role = "Tech Lead", DriverId = "driver-astra" });

        db.ProjectPersonas.AddRange(
            new ProjectPersona { ProjectId = ProjectId, PersonaId = "rook" },
            new ProjectPersona { ProjectId = ProjectId, PersonaId = "sorren" },
            new ProjectPersona { ProjectId = ProjectId, PersonaId = "astra" });

        db.AgentMemories.Add(AgentMemory.TryCreate("driver-rook", ProjectId,
            AgentMemoryCategories.Environment, "dotnet_test_flags", "dotnet test -v quiet --nologo")!);

        await db.SaveChangesAsync();

        if (withCheckpoint)
        {
            // After SaveChangesAsync: the repository resolves project_id by looking the job up in the
            // jobs table, so a checkpoint written before its job row exists is skipped outright. That
            // is deliberate in the repository (a checkpoint is orientation state, not correctness
            // state), but it means seeding one inside the same transaction silently produces no
            // checkpoint and a resume test that then passes.
            await new WorkspaceStateRepository(db).SaveCheckpointAsync(new TaskWorkingCheckpoint(
                JobId: JobId.ToString(),
                TaskId: TaskId.ToString(),
                ActiveBranch: "feat/layers",
                Status: JobTaskStatus.InProgress,
                LastSummary: "checkpointed the reconciler",
                ModifiedFiles: ["src/a.cs"],
                ActiveStash: null,
                UpdatedAt: DateTimeOffset.UtcNow.AddMinutes(-30),
                HeadCommit: "c0ffee1 wire: prompt stack",
                TurnSessionId: null));
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Points the checkpoint at <paramref name="sessionId"/>.
    /// </summary>
    /// <remarks>
    /// The composer reads the checkpoint's session first, so a resume test that seeds a transcript under
    /// a different id would be testing the metadata fallback rather than the resume path it names.
    /// </remarks>
    private static async Task MoveCheckpointToSessionAsync(AgentMeshDbContext db, Guid sessionId)
    {
        var checkpoints = new WorkspaceStateRepository(db);
        var existing = await checkpoints.GetCheckpointAsync(JobId.ToString(), TaskId.ToString());
        existing.Should().NotBeNull("SeedAsync(withCheckpoint: true) wrote one");

        await checkpoints.SaveCheckpointAsync(existing! with { TurnSessionId = sessionId.ToString() });
    }

    private static async Task SeedSessionAsync(AgentMeshDbContext db, Guid sessionId, string content)
    {
        db.AgentSessions.Add(new AgentSession
        {
            Id = sessionId,
            Title = "Resume turn",
            Status = "completed",
            PersonaId = "rook",
            DriverId = "driver-rook",
            CreatorAgentId = "driver-rook",
            MetadataJson = JsonSerializer.Serialize(new { jobId = JobId.ToString(), projectId = ProjectId }),
        });

        db.SessionMessages.Add(new SessionMessage
        {
            SessionId = sessionId,
            SenderAgentId = "driver-rook",
            EventType = "thought",
            Content = content,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        });

        await db.SaveChangesAsync();
    }
}
