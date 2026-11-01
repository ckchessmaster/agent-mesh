using System.Text.Json;
using AgentMesh.Core.Models;
using AgentMesh.Core.Repositories;
using AgentMesh.Core.Services;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Repositories;
using AgentMesh.Tests.Helpers;
using AgentMesh.Worker.Activities;
using AgentMesh.Worker.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Services;

/// <summary>
/// The 6-layer hierarchy: what lands in the cached prefix, what lands in the turn-0 envelope, and the
/// ordering that keeps the prefix reusable.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PromptStackBuilderTests"/> covers the mechanics inherited from Job D — the sliding window,
/// the memory read, checkpoint hydration, session resolution. This file covers what Job E adds: the
/// split itself, the layer boundaries, the team topology, and the wire contract.
/// </para>
/// <para>
/// The assertions deliberately straddle both halves of the stack. Asserting only on a flattened string
/// would pass on an implementation that put every layer in one block — which is the exact regression
/// this feature removes — so the tests that matter assert on
/// <see cref="AssembledPromptStack.CachedSystemPrompt"/> and
/// <see cref="AssembledPromptStack.UserTaskEnvelope"/> separately, and on what each must <em>not</em>
/// contain.
/// </para>
/// </remarks>
public class PromptStackLayeringTests
{
    private const string ProjectId = "agent-mesh";
    private static readonly Guid JobId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid TaskId = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");
    private static readonly Guid SessionId = Guid.Parse("cccccccc-3333-3333-3333-333333333333");

    private readonly string _dbName = "PromptLayers_" + Guid.NewGuid();

    private AgentMeshDbContext Db() => TestDbContextFactory.Create(_dbName);

    // ──────────────────────────────────────────────────────────────────────────
    // The spec's named tests
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Layers 1 through 5 are generated in strict order, separated by markdown rules, and nothing
    /// volatile is among them.
    /// </summary>
    /// <remarks>
    /// The order is the feature. A prefix cache matches on leading tokens, so a single per-turn value
    /// anywhere in the cached block invalidates every byte after it — which means "the layers are in the
    /// right order" and "no dynamic value is in this block" are the same assertion viewed from two
    /// sides, and both have to be pinned or an implementation can satisfy one and break the other.
    /// </remarks>
    [Fact]
    public async Task BuildPromptStack_OrdersLayers1Through5InSystemPrompt()
    {
        using var db = Db();
        await SeedAsync(db, roster: true, memories: true);

        var assembled = await BuildAsync(db);

        var cached = assembled.CachedSystemPrompt;

        var markers = new[]
        {
            "[LAYER 1: PLATFORM INVARIANTS]",
            "[LAYER 2: MASTER PERSONA]",
            "[LAYER 3: PROJECT MANIFEST]",
            "[LAYER 4: TEAM TOPOLOGY & ESCALATION PATHS]",
            "[LAYER 5: PERSISTENT MEMORY & NOTES",
        };

        var positions = markers.Select(m => cached.IndexOf(m, StringComparison.Ordinal)).ToList();

        positions.Should().OnlyContain(p => p >= 0,
            $"all five layers belong in the cached prefix; got:\n{cached}");
        positions.Should().BeInAscendingOrder(
            "volatility order is what makes the prefix reusable across turns");

        // The divider is a markdown rule, and the count pins it: four joins between five layers. An
        // implementation that switched to blank-line separation would still pass the ordering check
        // above while breaking the section boundaries a model uses to tell one layer's rules from the
        // next.
        cached.Split(["\n\n---\n\n"], StringSplitOptions.None).Should().HaveCount(5,
            "five layers joined by four markdown rules");

        // Layer 6 must not have crept into the prefix. Each of these changes between two turns of the
        // same task, so any one of them here costs the whole stack below it its cache hit.
        cached.Should().NotContain(PromptStackWirePayload.EnvelopeMarker)
            .And.NotContain(EnvironmentRecoveryNote.Header)
            .And.NotContain("[WORKING CHECKPOINT")
            .And.NotContain("[OBJECTIVE]")
            .And.NotContain("feat/layers", "the branch name is per-task and belongs in the envelope");

        // The envelope is the dynamic half, and it opens the user turn.
        assembled.UserTaskEnvelope.Should().StartWith(PromptStackWirePayload.EnvelopeMarker)
            .And.Contain("[OBJECTIVE]");
    }

    /// <summary>
    /// Saved memories and the resolved <c>/workspace/repos/{repo_name}</c> path both reach the prompt.
    /// </summary>
    /// <remarks>
    /// The path assertion is the multi-repo bug in miniature: a project whose
    /// <c>repository_name</c> is <c>payments-api</c> must not be prompted with the default
    /// <c>agent-mesh</c> directory, or the agent edits the wrong tree while every command still
    /// succeeds.
    /// </remarks>
    [Fact]
    public async Task BuildPromptStack_IncludesMemoriesAndDynamicPath()
    {
        using var db = Db();
        await SeedAsync(db, roster: false, memories: true, repositoryName: "payments-api");
        // working_directory left blank so the path must be derived from repository_name.

        // No workspace path on the context: the project row is what derives the directory, so leaving
        // the input blank is the only way to exercise the resolution.
        var assembled = await BuildAsync(db, Context(workspacePath: ""));

        assembled.MemoryCount.Should().Be(2);

        // Layer 5 carries the memories, in the spec's `[category/key]: value` shape.
        var cached = assembled.CachedSystemPrompt;
        cached.Should().Contain("[environment/dotnet_test_flags]: dotnet test -v quiet --nologo",
            "the spec fixes this rendering; it reads as a path into the memory store")
            .And.Contain("[build_and_test/vitest_mocking]");

        // The header names the project scope, so an agent can tell which bucket these came from.
        cached.Should().Contain($"[LAYER 5: PERSISTENT MEMORY & NOTES (project: {ProjectId})]");

        // The resolved working root, in both halves: Layer 3 because the directory is a property of the
        // repository and changes as often as that layer does, and the envelope because the envelope has
        // to stay readable after the conversation around it is compacted away.
        assembled.WorkspacePath.Should().Be("/workspace/repos/payments-api");
        cached.Should().Contain("/workspace/repos/payments-api");
        assembled.UserTaskEnvelope.Should().Contain("/workspace/repos/payments-api");
        assembled.UserTaskEnvelope.Should().NotContain("/workspace/repos/agent-mesh",
            "the default path must not appear once the project names a different repository");

        // Naming a directory only helps if the prompt also says not to go and check it.
        assembled.UserTaskEnvelope.Should().Contain("do not run pwd");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Layer 1: platform invariants
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Layer 1 states the execution rules and the tool names the agent is expected to call.
    /// </summary>
    [Fact]
    public void PlatformPrompt_CarriesTheEnforcedRulesAndToolNames()
    {
        var layer = PromptStackBuilder.PlatformPrompt();

        layer.Should().Contain("3-strike limit")
            .And.Contain("Self-merging your own pull request is forbidden")
            .And.Contain("save_memory").And.Contain("post_job_message")
            .And.Contain("manage_task").And.Contain("open_pull_request");

        // The sandbox numbers come from the code that enforces them. Hardcoding them here would let the
        // prompt and the runner disagree silently after an operator raises the ceiling, and the agent
        // would then read its own timeout as a bug in its command.
        layer.Should().Contain($"{CommandExecutionPolicy.DefaultTimeoutSeconds}s")
            .And.Contain(CommandOutputSpillover.RunsDirectory)
            .And.Contain("last_run.log");
    }

    /// <summary>
    /// Layer 1 is byte-identical across contexts, which is what makes it cacheable platform-wide.
    /// </summary>
    /// <remarks>
    /// The one layer with no inputs at all. If it ever grows a project id or a timestamp it stops being
    /// shareable across agents and projects, and the cross-project cache it is the sole beneficiary of
    /// disappears without a test failing anywhere.
    /// </remarks>
    [Fact]
    public void PlatformPrompt_IsIdenticalForEveryAgentAndProject()
    {
        PromptStackBuilder.PlatformPrompt().Should().Be(PromptStackBuilder.PlatformPrompt());

        var a = PromptStackBuilder.BuildCachedSystemPrompt(
            new PromptStackContext("agent-a", "proj-a", "job-a", "task-a", "/w/a", "main", "go"),
            project: null, persona: null, [], [], "/w/a");
        var b = PromptStackBuilder.BuildCachedSystemPrompt(
            new PromptStackContext("agent-b", "proj-b", "job-b", "task-b", "/w/b", "dev", "stop"),
            project: null, persona: null, [], [], "/w/b");

        // Same everything-else: Layer 1 must be the identical slice of both, so an agent on another
        // project still hits the same cached prefix bytes.
        var layerOneA = a[..a.IndexOf("[LAYER 2", StringComparison.Ordinal)];
        var layerOneB = b[..b.IndexOf("[LAYER 2", StringComparison.Ordinal)];
        layerOneA.Should().Be(layerOneB, "Layer 1 is shared across every agent and project");

        // And the divergence must begin exactly there — otherwise the comparison above is vacuous and
        // both stacks are simply identical, which would mean the persona and workspace are being
        // ignored entirely.
        a.Should().NotBe(b);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Layer 4: team topology
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The roster is rendered from the project's personas, with escalation paths attached by role.
    /// </summary>
    /// <remarks>
    /// Rendered from data rather than a hardcoded cast list, because a prompt that names a lead who left
    /// three months ago sends escalations into a void while the agent reports "escalated" — the
    /// confidently-wrong failure this platform keeps re-diagnosing.
    /// </remarks>
    [Fact]
    public async Task TeamPrompt_RendersTheRosterWithEscalationPaths()
    {
        using var db = Db();
        await SeedAsync(db, roster: true, memories: false);

        var cached = (await BuildAsync(db)).CachedSystemPrompt;

        cached.Should().Contain("[TEAM ROSTER & ESCALATION PATHS]")
            .And.Contain("Principal Architect: Sorren")
            .And.Contain("Tech Lead: Astra")
            .And.Contain("Security Lead: Vane");

        // The responsibility is matched from the role text, so an escalation names someone who can
        // actually act on it.
        cached.Should().Contain("schema migrations")
            .And.Contain("3-strike failures")
            .And.Contain("vulnerability findings");
    }

    /// <summary>
    /// The agent building the prompt is excluded as an escalation target.
    /// </summary>
    /// <remarks>
    /// The self-escalation deadlock: an agent told to escalate to the tech lead while it <em>is</em> the
    /// tech lead posts to its own queue and waits for a reply from itself. It looks like an unresponsive
    /// teammate and never resolves, because nothing is coming.
    /// </remarks>
    [Fact]
    public async Task TeamPrompt_ExcludesTheAgentItselfAsAnEscalationTarget()
    {
        using var db = Db();
        await SeedAsync(db, roster: true, memories: false);

        // Same persona, this time as the one composing the stack.
        var cached = (await BuildAsync(db, Context(personaId: "rook"))).CachedSystemPrompt;

        cached.Should().Contain("Rook")
            .And.Contain("this is you; never escalate to yourself");

        var rookLine = cached.Split('\n').First(l => l.Contains("Software Engineer: Rook"));
        rookLine.Should().NotContain("\u2014 escalate",
            "the self row must not name an escalation target; the only text it may carry about " +
            "escalation is the instruction not to escalate to itself");
        rookLine.Should().Contain("this is you");
    }

    /// <summary>
    /// A role the matcher does not recognise renders with no escalation clause.
    /// </summary>
    /// <remarks>
    /// Guessing here is worse than saying nothing: naming the wrong target makes the agent report
    /// "escalated to the architect" about a persona that cannot approve a schema change, and the
    /// human reading that message has no way to tell the escalation went nowhere.
    /// </remarks>
    [Theory]
    [InlineData("Principal Architect", "schema migrations")]
    [InlineData("Security Lead", "auth boundaries")]
    [InlineData("Tech Lead", "test deadlocks")]
    [InlineData("Engineering Manager", "test deadlocks")]
    [InlineData("Product Owner", "product scope")]
    [InlineData("Documentation Writer", null)]
    public void EscalationForRole_MatchesKnownRolesOnly(string role, string? expected)
    {
        var actual = PromptStackBuilder.EscalationForRole(role);

        if (expected is null)
        {
            actual.Should().BeNull("an unrecognised role gets no escalation clause rather than a guessed one");
            return;
        }

        actual.Should().NotBeNull().And.Contain(expected);
    }

    /// <summary>
    /// A compound role resolves to its domain, not to its seniority word.
    /// </summary>
    /// <remarks>
    /// "Principal Architect" and "Security Lead" both contain a leadership token, and the matcher tests
    /// the domain first. Invert the checks and a schema-migration escalation routes to the security
    /// lead, who cannot approve it — and the agent reports the escalation as complete.
    /// </remarks>
    [Theory]
    [InlineData("Security Architect", "auth boundaries")]
    [InlineData("Lead Architect", "schema migrations")]
    public void EscalationForRole_PrefersTheDomainOverTheSeniorityWord(string role, string expected)
    {
        PromptStackBuilder.EscalationForRole(role)!.Should().Contain(expected);
    }

    /// <summary>
    /// With no roster assigned, the layer says so and routes to the operator.
    /// </summary>
    [Fact]
    public async Task TeamPrompt_WithoutARosterRoutesEscalationsToTheOperator()
    {
        using var db = Db();
        await SeedAsync(db, roster: false, memories: false);

        var cached = (await BuildAsync(db)).CachedSystemPrompt;

        cached.Should().Contain("No roster is assigned to this project")
            .And.Contain("Escalate to the operator in Job Chat",
                "an unaddressed message may not resume anyone, so the layer has to say who to address");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Layer 2 / 3 fallbacks
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A missing persona row degrades to a conservative default rather than dropping the layer.
    /// </summary>
    /// <remarks>
    /// The layer boundary stays in place so the cache layout does not shift for every other agent, and
    /// the agent is told outright that its identity is unavailable — an agent that assumes it has
    /// persona guidance it does not have will act on permissions it was never granted.
    /// </remarks>
    [Fact]
    public void PersonaPrompt_WithoutARowStatesTheGapAndDefaults()
    {
        var layer = PromptStackBuilder.PersonaPrompt(null, Context(personaId: "ghost"));

        layer.Should().Contain("No persona row was found")
            .And.Contain("ghost")
            .And.Contain("escalate");
    }

    /// <summary>
    /// A persona with an empty prompt still gets the 3-R standard.
    /// </summary>
    [Fact]
    public void PersonaPrompt_WithEmptyTextStillAppliesThe3RStandard()
    {
        var layer = PromptStackBuilder.PersonaPrompt(
            new Persona { Id = "rook", Name = "Rook", Role = "Engineer", PersonaPrompt = "   " },
            Context());

        layer.Should().Contain("3-R")
            .And.Contain("Result")
            .And.Contain("Reasoning")
            .And.Contain("Risk");
    }

    /// <summary>
    /// A persona's own text is emitted verbatim rather than paraphrased.
    /// </summary>
    [Fact]
    public void PersonaPrompt_EmitsTheAuthoredTextUnchanged()
    {
        const string authored = "Reproduce first, verify always, leave the plumbing cleaner than you found it.";

        PromptStackBuilder.PersonaPrompt(
            new Persona { Id = "rook", Name = "Rook", PersonaPrompt = authored }, Context())
            .Should().Contain(authored,
                "the persona is authored by a human; re-summarising it silently edits someone's agent");
    }

    /// <summary>
    /// A project with no instructions tells the agent to discover the contract and save it.
    /// </summary>
    [Fact]
    public void ProjectPrompt_WithoutInstructionsTellsTheAgentToPersistWhatItLearns()
    {
        var layer = PromptStackBuilder.ProjectPrompt(
            new Project { Id = ProjectId, Name = "Agent Mesh", ProjectInstructions = "" }, "/workspace/repos/agent-mesh");

        layer.Should().Contain("No project instructions are recorded")
            .And.Contain("AGENTS.md")
            .And.Contain("save_memory",
                "the next turn should start where this one finished, not re-derive the same contract");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Layer 5: memory rendering and the cap
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An uncategorised memory renders without a dangling slash.
    /// </summary>
    /// <remarks>
    /// <c>save_memory</c> accepts a category but does not require one, so a blank category is the
    /// common case rather than an edge case. Rendering it as <c>[/key]</c> puts an empty heading where a
    /// reader expects a label.
    /// </remarks>
    [Fact]
    public void MemoryPrompt_OmitsTheSlashForAnUncategorisedMemory()
    {
        var layer = PromptStackBuilder.MemoryPrompt(["[bare_key]: a lesson with no category"], Context());

        layer.Should().Contain("* [bare_key]: a lesson with no category")
            .And.NotContain("[/");
    }

    /// <summary>
    /// An empty memory list still renders the layer, with the instruction to start saving.
    /// </summary>
    /// <remarks>
    /// The layer boundary must not appear and disappear between turns: a section that comes and goes
    /// shifts every byte after it, which is the cache invalidation this whole file is about.
    /// </remarks>
    [Fact]
    public void MemoryPrompt_WithNoMemoriesKeepsTheLayerInPlace()
    {
        var layer = PromptStackBuilder.MemoryPrompt([], Context());

        layer.Should().Contain("[LAYER 5: PERSISTENT MEMORY & NOTES")
            .And.Contain("No memories saved")
            .And.Contain("save_memory");
    }

    /// <summary>
    /// The memory list is capped, and the number dropped is reported.
    /// </summary>
    /// <remarks>
    /// <see cref="IAgentMemoryRepository.GetMemoriesForAgentAsync"/> is unbounded by design. Without a
    /// cap, a diligent agent eventually has more memories than the prompt can carry, and Layer 5 crowds
    /// out the envelope and the window — the sections that stop this turn from re-running
    /// <c>git status</c>. The count is stated rather than the omission hidden, because an agent that
    /// knows it has unread notes will ask for them.
    /// </remarks>
    [Fact]
    public void CapMemories_LimitsTheListAndReportsTheOmission()
    {
        var lines = Enumerable.Range(0, PromptStackBuilder.MaxRenderedMemories + 25)
            .Select(i => $"[env/k{i:D3}]: lesson {i}")
            .ToList();

        var capped = PromptStackBuilder.CapMemories(lines);

        capped.Should().HaveCount(PromptStackBuilder.MaxRenderedMemories + 1,
            "the cap plus exactly one marker line");
        capped.Take(PromptStackBuilder.MaxRenderedMemories).Should().Equal(lines.Take(PromptStackBuilder.MaxRenderedMemories),
            "the repository orders by category then recency, so the head of the list is the freshest");
        capped[^1].Should().Contain("25 more saved memories not shown");
    }

    [Fact]
    public void CapMemories_LeavesAShortListUntouched()
    {
        var lines = Enumerable.Range(0, PromptStackBuilder.MaxRenderedMemories)
            .Select(i => $"[env/k{i}]: v")
            .ToList();

        PromptStackBuilder.CapMemories(lines).Should().Equal(lines);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Layer 6: the objective
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A macro turn with no free-text objective still gets an imperative one.
    /// </summary>
    /// <remarks>
    /// The common case: the workflow input names a task, not a sentence. An envelope whose objective is
    /// empty is precisely the payload that produces "The user hasn't asked a question yet… I will wait",
    /// so every level of missing data has to land on something actionable.
    /// </remarks>
    [Fact]
    public void ResolveObjective_WithNoUserPromptFallsBackToTheFocusTask()
    {
        var focus = new JobTaskDto { Id = TaskId, Title = "Add the wire contract" };

        var objective = PromptStackBuilder.ResolveObjective(Context(userPrompt: "   "), focus);

        objective.Should().Contain("Add the wire contract")
            .And.Contain("Do not reply that you are waiting for instructions",
                "the envelope is the instruction, and saying so is what breaks the idle loop");
    }

    /// <summary>
    /// With neither a prompt nor a resolvable task, the objective is still an instruction.
    /// </summary>
    [Fact]
    public void ResolveObjective_WithNothingAtAllStillCommandsAnAction()
    {
        var objective = PromptStackBuilder.ResolveObjective(Context(userPrompt: ""), focus: null);

        objective.Should().NotBeNullOrWhiteSpace()
            .And.Contain("Continue the task");
    }

    /// <summary>
    /// A real operator prompt wins over the checklist fallback.
    /// </summary>
    [Fact]
    public void ResolveObjective_PrefersTheExplicitUserPrompt()
    {
        PromptStackBuilder.ResolveObjective(
                Context(userPrompt: "Stop the migration and revert commit 3."),
                new JobTaskDto { Title = "Add the wire contract" })
            .Should().Be("Stop the migration and revert commit 3.");
    }

    /// <summary>
    /// A task id that matches no checklist item is not silently relabelled onto a different task.
    /// </summary>
    /// <remarks>
    /// The tempting fallback — "show the first incomplete item" — would put the wrong acceptance
    /// criteria in front of an agent that is working on the right one, which is a wrong prompt that
    /// looks like a correct one.
    /// </remarks>
    [Fact]
    public void ResolveFocusTask_WithAnUnknownIdReturnsNullRatherThanAnotherTask()
    {
        var job = new ProjectJob
        {
            TasksJson = JsonSerializer.Serialize(new[]
            {
                new JobTaskDto { Id = Guid.NewGuid(), Title = "Some other item" },
            }),
        };

        PromptStackBuilder.ResolveFocusTask(job, TaskId.ToString()).Should().BeNull();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // The wire contract
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A complete stack projects onto the wire form; an incomplete one does not project at all.
    /// </summary>
    /// <remarks>
    /// The null return is the load-bearing half. A payload with an envelope but no prefix would have the
    /// runner send <c>messages[1]</c> with no <c>messages[0]</c>: an agent handed a task with no
    /// persona, no conventions and no memories. Returning null makes the runner take the legacy path,
    /// which is the known-good behaviour it ships with today.
    /// </remarks>
    [Fact]
    public void WirePayload_RequiresBothHalvesOrProjectsToNull()
    {
        var complete = new AssembledPromptStack(
            "[LAYER 1: PLATFORM INVARIANTS] prefix", "[TASK ENVELOPE] envelope",
            Array.Empty<TurnHistoryMessage>(), "/w");

        PromptStackWirePayload.From(complete)!.CachedSystemPrompt.Should().Be("[LAYER 1: PLATFORM INVARIANTS] prefix");

        var noPrefix = complete with { CachedSystemPrompt = "  " };
        var noEnvelope = complete with { UserTaskEnvelope = "" };

        PromptStackWirePayload.From(noPrefix).Should().BeNull();
        PromptStackWirePayload.From(noEnvelope).Should().BeNull();
        PromptStackWirePayload.From(null).Should().BeNull();
    }

    /// <summary>
    /// The history budget trims from the oldest end and says how much it removed.
    /// </summary>
    /// <remarks>
    /// The dispatcher broadcasts to <c>Clients.All</c> as well as the two targeted groups, so every
    /// connected runner receives every byte. Per-message caps alone do not bound that: 15 messages at
    /// 4,000 chars is 60 KB of history multiplied by every agent on the mesh, per turn.
    /// </remarks>
    [Fact]
    public void WirePayload_TrimsHistoryFromTheOldestEndAndReportsIt()
    {
        // Five turns well past the budget, so the trim has to give something up.
        var big = new string('x', 20_000);
        var history = Enumerable.Range(0, 5)
            .Select(i => new TurnHistoryMessage("assistant", $"msg-{i} {big}"))
            .ToList();

        history.Sum(m => m.Content!.Length).Should().BeGreaterThan(PromptStackWirePayload.MaxHistoryContentChars,
            "the fixture must actually exceed the budget or the assertion below proves nothing");

        var bounded = PromptStackWirePayload.BoundHistory(history)!;

        bounded.Should().Contain(m => m.Content!.StartsWith("msg-4"),
            "the newest turn is the one that answers 'what was I doing'");
        bounded.Should().NotContain(m => m.Content!.StartsWith("msg-0"),
            "the oldest turn is the first to go");
        bounded[0].Content.Should().Contain("omitted from this turn's context window",
            "an agent that believes it saw everything will redo work that is already done");

        bounded.Sum(m => m.Content!.Length).Should().BeLessThanOrEqualTo(PromptStackWirePayload.MaxHistoryContentChars);

        // Order survives the trim: walking backwards to spend the budget must not hand the model the
        // transcript newest-first.
        bounded.Where(m => m.EventType != "compaction_note")
            .Select(m => m.Content![..5])
            .Should().BeInAscendingOrder();
    }

    /// <summary>
    /// The omission marker's own text is inside the budget, not added on top of it.
    /// </summary>
    /// <remarks>
    /// The ceiling is only a ceiling if the function that enforces it respects it. Reserving the full
    /// budget for messages and then appending the marker would overshoot by ~150 characters on every
    /// trimmed window — invisible against a 48 KB payload, and exactly the kind of "the cap is
    /// approximately the cap" drift that makes a later reader trust a number nothing enforces.
    /// </remarks>
    [Fact]
    public void WirePayload_ReservesRoomForTheOmissionMarker()
    {
        var budget = PromptStackWirePayload.MaxHistoryContentChars;

        // Two messages that together land within a handful of characters of the ceiling, so the loop
        // keeps both and the marker is the only thing that could push the total over.
        var history = new List<TurnHistoryMessage>
        {
            new("user", new string('a', 10)),
            new("assistant", new string('b', budget - 10)),
            new("user", "x"),
        };

        var bounded = PromptStackWirePayload.BoundHistory(history)!;

        bounded.Sum(m => m.Content!.Length).Should().BeLessThanOrEqualTo(budget,
            "the marker line is part of the payload it is describing");
        bounded.Should().Contain(m => m.EventType == "compaction_note");
    }

    /// <summary>
    /// A single message larger than the budget is still carried.
    /// </summary>
    /// <remarks>
    /// The degenerate case the trim loop would otherwise get wrong by returning nothing but an apology.
    /// The composer caps each message far below this ceiling, so reaching it means a window was written
    /// by a producer that does not — and the useful response is to send the turn that matters rather
    /// than an empty transcript with a note about it.
    /// </remarks>
    [Fact]
    public void WirePayload_NeverTrimsAwayTheNewestMessage()
    {
        var oversized = new string('z', PromptStackWirePayload.MaxHistoryContentChars * 2);
        var history = new List<TurnHistoryMessage>
        {
            new("user", "oldest"),
            new("assistant", oversized),
        };

        var bounded = PromptStackWirePayload.BoundHistory(history)!;

        bounded.Should().Contain(m => m.Content == oversized,
            "an oversized newest turn beats an empty window");
        bounded[^1].Content.Should().Be(oversized, "the surviving turn stays last");
    }

    [Fact]
    public void WirePayload_LeavesAWindowedHistoryAlone()
    {
        var history = new[] { new TurnHistoryMessage("user", "short"), new TurnHistoryMessage("assistant", "brief") };

        PromptStackWirePayload.BoundHistory(history).Should().Equal(history,
            "a window inside the budget must not be rewritten at all");
        PromptStackWirePayload.BoundHistory(null).Should().BeNull();
    }

    /// <summary>
    /// The stack survives the JSON round trip it takes over the wire.
    /// </summary>
    /// <remarks>
    /// The payload is serialised by the worker, deserialised into a string field by the API, then
    /// re-serialised into the SignalR broadcast and deserialised again by the CLI. Case handling that is
    /// wrong anywhere in that chain arrives as a null stack on every turn — the silent fallback, which
    /// looks like the feature was never built.
    /// </remarks>
    [Fact]
    public void WirePayload_SurvivesTheSerialisationRoundTrip()
    {
        var payload = new PromptStackWirePayload(
            "prefix layers 1-5",
            PromptStackWirePayload.EnvelopeMarker + "\n* Task ID: #7",
            [new TurnHistoryMessage("assistant", "prior thought", "thought")]);

        // camelCase, matching the dispatcher's anonymous-object payload and the CLI's read of it.
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        json.Should().Contain("\"cachedSystemPrompt\"")
            .And.Contain("\"userTaskEnvelope\"")
            .And.Contain("\"history\"");

        var back = JsonSerializer.Deserialize<PromptStackWirePayload>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        back!.CachedSystemPrompt.Should().Be(payload.CachedSystemPrompt);
        back.UserTaskEnvelope.Should().Be(payload.UserTaskEnvelope);
        back.History.Should().ContainSingle()
            .Which.Content.Should().Be("prior thought");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Prefix stability across turns
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Two turns of the same task produce a byte-identical cached prefix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The actual cache claim, asserted directly. Everything else in this file is structure; this is the
    /// property the structure exists to deliver. The second turn differs in every dynamic way — a new
    /// reply, a fresh git probe, a longer transcript — and the prefix must not notice.
    /// </para>
    /// <para>
    /// The reconciler is swapped between the two builds to prove the point: if the environment note or
    /// the dirty count leaked into the prefix, the byte comparison would fail here rather than in
    /// production after a month of billed re-tokenisation.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task CachedPrefix_IsByteIdenticalAcrossTurnsOfTheSameTask()
    {
        using var db = Db();
        await SeedAsync(db, roster: true, memories: true);

        var clean = Probe("0", "");
        var dirty = Probe("4", "M src/a.cs");

        var turnOne = await BuildAsync(db, Context(), clean);
        var turnTwo = await BuildAsync(db,
            Context() with { PendingReply = new ChatMessageSignal("astra", "use tabs", DateTimeOffset.UtcNow) },
            dirty);

        turnTwo.CachedSystemPrompt.Should().Be(turnOne.CachedSystemPrompt,
            "the prefix is the cache key; any byte that moves costs every layer below it");

        // And the halves that are supposed to differ do differ, so the equality above is not the trivial
        // result of both turns producing nothing.
        turnTwo.UserTaskEnvelope.Should().NotBe(turnOne.UserTaskEnvelope);
        turnTwo.UserTaskEnvelope.Should().Contain("use tabs");
        turnTwo.EnvironmentNote.Should().NotBe(turnOne.EnvironmentNote);
    }

    /// <summary>
    /// The roster renders in a stable order regardless of how the database returned it.
    /// </summary>
    /// <remarks>
    /// The query orders by name for this reason. An unordered read would reshuffle the block between
    /// turns whenever Postgres changed its plan, and every byte from Layer 4 down would miss on a
    /// schedule nobody can reproduce.
    /// </remarks>
    [Fact]
    public void TeamPrompt_IsDeterministicForTheSameRoster()
    {
        var roster = new[]
        {
            new PromptStackBuilder.RosterMember("vane", "Vane", "Security Lead", null, "Tier2", false),
            new PromptStackBuilder.RosterMember("astra", "Astra", "Tech Lead", null, "Tier1", false),
        };

        var forward = PromptStackBuilder.TeamPrompt(roster, Context());
        var reversed = PromptStackBuilder.TeamPrompt(roster.Reverse().ToList(), Context());

        forward.Should().Be(reversed, "the rendered roster is byte-stable, so the layer stays cacheable");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Degradation
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A memory store that throws degrades Layer 5 and leaves the rest of the stack intact.
    /// </summary>
    /// <remarks>
    /// The alternative is that an outage in one subsystem takes task execution down with it, which is a
    /// worse failure than a turn with a shorter prompt.
    /// </remarks>
    [Fact]
    public async Task Build_WhenTheMemoryStoreThrows_StillProducesEveryLayer()
    {
        using var db = Db();
        await SeedAsync(db, roster: true, memories: true);

        var exploding = new Mock<IAgentMemoryRepository>();
        exploding.Setup(r => r.GetMemoriesForAgentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection refused"));

        var assembled = await new PromptStackBuilder(
            db,
            new WorkspaceStateRepository(db),
            new ShellWorkspaceReconciler(shellRunner: (_, _, _) => Task.FromResult(Probe("0", ""))),
            null,
            exploding.Object)
            .BuildPromptStackAsync(Context());

        assembled.MemoryCount.Should().Be(0);
        assembled.CachedSystemPrompt.Should().Contain("[LAYER 1: PLATFORM INVARIANTS]")
            .And.Contain("[LAYER 2: MASTER PERSONA]")
            .And.Contain("[LAYER 3: PROJECT MANIFEST]")
            .And.Contain("[LAYER 4: TEAM TOPOLOGY & ESCALATION PATHS]")
            .And.Contain("[LAYER 5: PERSISTENT MEMORY & NOTES",
                "the layer stays in place so the cache layout does not shift for the turns that follow");
        assembled.UserTaskEnvelope.Should().Contain(PromptStackWirePayload.EnvelopeMarker);
    }

    /// <summary>
    /// A workspace that cannot be probed costs the recovery note and nothing else.
    /// </summary>
    [Fact]
    public async Task Build_WhenTheProbeFails_OmitsTheNoteButKeepsTheEnvelope()
    {
        using var db = Db();
        await SeedAsync(db, roster: false, memories: false);

        var assembled = await BuildAsync(db, Context(),
            new CommandExecutionResult(false, 127, "git: not found"));

        assembled.EnvironmentNote.Should().BeNull();
        assembled.UserTaskEnvelope.Should().NotContain(EnvironmentRecoveryNote.Header)
            .And.Contain(PromptStackWirePayload.EnvelopeMarker)
            .And.Contain("[OBJECTIVE]");
    }

    /// <summary>
    /// A missing project row still yields a usable stack.
    /// </summary>
    [Fact]
    public async Task Build_WithNoProjectRow_StillComposesAllSixLayers()
    {
        using var db = Db();
        // Deliberately no SeedAsync: nothing exists in the database at all.

        var assembled = await BuildAsync(db, Context(workspacePath: "/workspace/repos/ghost"));

        assembled.CachedSystemPrompt.Should().Contain("[LAYER 3: PROJECT MANIFEST]")
            .And.Contain("(no project row; conventions unavailable)");
        assembled.UserTaskEnvelope.Should().Contain("/workspace/repos/ghost")
            .And.Contain("[OBJECTIVE]");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static PromptStackContext Context(
        string workspacePath = "/workspace/repos/agent-mesh",
        string personaId = "rook",
        string userPrompt = "") =>
        new(
            AgentId: "driver-rook",
            ProjectId: ProjectId,
            JobId: JobId.ToString(),
            TaskId: TaskId.ToString(),
            WorkspacePath: workspacePath,
            TargetBranch: "main",
            UserPrompt: userPrompt,
            PersonaId: personaId,
            TurnSessionId: SessionId.ToString());

    private static Task<AssembledPromptStack> BuildAsync(
        AgentMeshDbContext db,
        PromptStackContext? context = null,
        CommandExecutionResult? probe = null) =>
        new PromptStackBuilder(
            db,
            new WorkspaceStateRepository(db),
            new ShellWorkspaceReconciler(shellRunner: (_, _, _) =>
                Task.FromResult(probe ?? Probe("0", ""))),
            null,
            new AgentMemoryRepository(db))
        .BuildPromptStackAsync(context ?? Context());

    /// <summary>A reconciler probe result, shaped by the marker protocol the reconciler parses.</summary>
    private static CommandExecutionResult Probe(string dirtyCount, string files) =>
        new(true, 0, $$"""
            @@HEADTYPE@@
            branch
            @@BRANCH@@
            feat/layers
            @@UPSTREAM@@
            origin/feat/layers
            @@COUNT@@
            {{dirtyCount}}
            @@FILES@@
            {{files}}
            @@STASH@@
            @@HEAD@@
            c0ffee1 wire: prompt stack
            @@DONE@@
            """);

    private static async Task SeedAsync(
        AgentMeshDbContext db,
        bool roster,
        bool memories,
        string repositoryName = "agent-mesh",
        // Blank by design: a stored absolute path would short-circuit the derivation the layering test
        // is trying to exercise. RepositoryName is what drives /workspace/repos/{repo_name}.
        string workingDirectory = "")
    {
        db.Projects.Add(new Project
        {
            Id = ProjectId,
            Name = "Agent Mesh",
            RepositoryName = repositoryName,
            WorkingDirectory = workingDirectory,
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

        db.Personas.Add(new Persona
        {
            Id = "rook",
            Name = "Rook",
            Role = "Software Engineer",
            PersonaPrompt = "Reproduce before fixing.",
            DriverId = "driver-rook",
        });

        if (roster)
        {
            db.Personas.AddRange(
                new Persona { Id = "astra", Name = "Astra", Role = "Tech Lead" },
                new Persona { Id = "sorren", Name = "Sorren", Role = "Principal Architect" },
                new Persona { Id = "vane", Name = "Vane", Role = "Security Lead" });

            db.ProjectPersonas.AddRange(
                new ProjectPersona { ProjectId = ProjectId, PersonaId = "rook" },
                new ProjectPersona { ProjectId = ProjectId, PersonaId = "astra" },
                new ProjectPersona { ProjectId = ProjectId, PersonaId = "sorren" },
                new ProjectPersona { ProjectId = ProjectId, PersonaId = "vane" });
        }

        if (memories)
        {
            db.AgentMemories.Add(AgentMemory.TryCreate("driver-rook", ProjectId,
                AgentMemoryCategories.Environment, "dotnet_test_flags", "dotnet test -v quiet --nologo")!);
            db.AgentMemories.Add(AgentMemory.TryCreate("driver-rook", ProjectId,
                AgentMemoryCategories.BuildAndTest, "vitest_mocking",
                "Tests mounting App.tsx must mock useSignalR")!);
        }

        db.AgentSessions.Add(new AgentSession
        {
            Id = SessionId,
            Title = "Prompt stack turn",
            Status = "thinking",
            PersonaId = "rook",
            DriverId = "driver-rook",
            CreatorAgentId = "driver-rook",
            MetadataJson = JsonSerializer.Serialize(new { jobId = JobId.ToString(), projectId = ProjectId }),
        });

        await db.SaveChangesAsync();
    }
}
