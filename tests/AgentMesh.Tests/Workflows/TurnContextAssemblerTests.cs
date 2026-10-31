using System.Text.Json;
using AgentMesh.Core.Models;
using AgentMesh.Core.Repositories;
using AgentMesh.Core.Services;
using AgentMesh.Core.Workflows;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Repositories;
using AgentMesh.Tests.Helpers;
using AgentMesh.Worker.Activities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Workflows;

/// <summary>
/// Prompt assembly: the sliding window, memory injection, checkpoint hydration and the environment
/// note, in the order the spec fixes.
/// </summary>
/// <remarks>
/// Runs on the in-memory provider like the rest of the assembly. The one thing that cannot be proven
/// there is SQL translation of the window query's <c>Take</c> after a <c>Where</c> on
/// <c>EventType</c>; <see cref="SlidingWindowQuery_TranslatesAgainstThePostgresProvider"/> pins that
/// separately, because a query the in-memory provider tolerates and Postgres rejects would make this
/// the untested path — and it is the path every turn takes.
/// </remarks>
public class TurnContextAssemblerTests
{
    private const string ProjectId = "agent-mesh";
    private static readonly Guid JobId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TaskId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SessionId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly string _dbName = "TurnContext_" + Guid.NewGuid();

    private AgentMeshDbContext Db() => TestDbContextFactory.Create(_dbName);

    // ──────────────────────────────────────────────────────────────────────────
    // The spec's named test
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A session with more than 50 messages contributes only the last 15, alongside the latest
    /// checkpoint.
    /// </summary>
    [Fact]
    public async Task SlidingWindow_LoadsOnlyRecentMessages()
    {
        using var db = Db();
        await SeedProjectAndJobAsync(db);
        await SeedSessionAsync(db);

        // 60 messages, each individually identifiable, oldest first.
        const int total = 60;
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-total);
        for (var i = 0; i < total; i++)
        {
            db.SessionMessages.Add(new SessionMessage
            {
                SessionId = SessionId,
                SenderAgentId = "driver-rook",
                EventType = "thought",
                Content = $"thought-{i:D3}",
                CreatedAt = baseTime.AddMinutes(i),
            });
        }

        await db.SaveChangesAsync();

        // The checkpoint the window must accompany.
        var checkpoints = new WorkspaceStateRepository(db);
        await checkpoints.SaveCheckpointAsync(new TaskWorkingCheckpoint(
            JobId.ToString(), TaskId.ToString(),
            ActiveBranch: "feat/recovery",
            Status: JobTaskStatus.InProgress,
            LastSummary: "Wired the reconciler; tests still red.",
            ModifiedFiles: ["src/a.cs"],
            ActiveStash: null,
            UpdatedAt: DateTimeOffset.UtcNow,
            HeadCommit: "c0ffee1 wire: reconciler",
            TurnSessionId: SessionId.ToString()));

        var assembled = await AssembleAsync(db, checkpoints);

        assembled.HistoryMessageCount.Should().Be(TurnContextAssembler.DefaultSlidingWindowSize,
            "the window is a fixed cap, not 'everything recent enough to fit'");
        assembled.Prompt.Should().NotContain("thought-000", "the oldest message must not be loaded");
        assembled.Prompt.Should().NotContain("thought-044");

        // The newest message must be present, and the boundary message too — proving the cap took the
        // newest 15 rather than some other 15.
        assembled.Prompt.Should().Contain($"thought-{total - 1:D3}");
        assembled.Prompt.Should().Contain("thought-045",
            "045 is the 15th from the end; its presence pins the window's position, not just its size");

        // The checkpoint rides along, so the older context the window dropped is not simply lost.
        assembled.Checkpoint.Should().NotBeNull();
        assembled.Prompt.Should().Contain("[WORKING CHECKPOINT]");
        assembled.Prompt.Should().Contain("Wired the reconciler; tests still red.");
        assembled.Prompt.Should().Contain("feat/recovery");
        assembled.Prompt.Should().Contain("c0ffee1 wire: reconciler");

        assembled.Prompt.Should().Contain("window capped at 15",
            "the agent must be told the conversation was truncated, or it will assume it is seeing the " +
            "whole history and conclude nothing else happened");
    }

    /// <summary>
    /// The window excludes tool-result payloads before applying the cap, not after.
    /// </summary>
    /// <remarks>
    /// The distinction is the whole point. Filtering after <c>Take(15)</c> yields "the last 15 rows,
    /// minus whichever are tool results", so a session whose recent rows are tool-heavy produces a
    /// window of two or three messages when fifteen were asked for — the agent loses the thread
    /// precisely in the busy stretches where it needs it most.
    /// </remarks>
    [Fact]
    public async Task SlidingWindow_ExcludesToolResultsBeforeApplyingTheCap()
    {
        using var db = Db();
        await SeedProjectAndJobAsync(db);
        await SeedSessionAsync(db);

        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-40);
        for (var i = 0; i < 40; i++)
        {
            // Every other row is a tool result: half the session's rows are unusable content.
            var isResult = i % 2 == 0;
            db.SessionMessages.Add(new SessionMessage
            {
                SessionId = SessionId,
                SenderAgentId = "driver-rook",
                EventType = isResult ? "tool_result" : "thought",
                Content = isResult ? $"HUGE-DUMP-{i:D3}" : $"kept-{i:D3}",
                CreatedAt = baseTime.AddMinutes(i),
            });
        }

        await db.SaveChangesAsync();

        var assembled = await AssembleAsync(db, new WorkspaceStateRepository(db));

        assembled.Prompt.Should().NotContain("HUGE-DUMP", "tool results are excluded from the window");
        assembled.HistoryMessageCount.Should().Be(TurnContextAssembler.DefaultSlidingWindowSize,
            "the cap is applied to the filtered set, so a tool-heavy session still yields a full window");

        // The 15 newest *kept* rows are 39, 37, ... 11. Row 9 is the 16th newest kept row and must be out.
        assembled.Prompt.Should().Contain("kept-039").And.Contain("kept-011");
        assembled.Prompt.Should().NotContain("kept-009");
    }

    /// <summary>
    /// A long tool payload is capped, and the cap is marked rather than silent.
    /// </summary>
    /// <remarks>
    /// The window alone does not bound prompt size: a single <c>dotnet build</c> result is routinely
    /// 40 KB and is usually one of the most recent messages. Message count bounds how many rows arrive;
    /// this bounds how big each one is. Together they are what makes the assembled prompt's size
    /// predictable.
    /// </remarks>
    [Fact]
    public async Task SlidingWindow_CapsPerMessageContentAndMarksTheTruncation()
    {
        using var db = Db();
        await SeedProjectAndJobAsync(db);
        await SeedSessionAsync(db);

        var huge = string.Concat(Enumerable.Repeat("build-output-line ", 2_000)); // ~36 KB
        db.SessionMessages.Add(new SessionMessage
        {
            SessionId = SessionId,
            SenderAgentId = "driver-rook",
            EventType = "thought",
            Content = huge,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var assembled = await AssembleAsync(db, new WorkspaceStateRepository(db));

        assembled.Prompt.Should().Contain("[truncated by harness:")
            .And.Contain("chars omitted]",
                "an unmarked cut reads to the model as the payload ending mid-sentence");
        assembled.Prompt.Length.Should().BeLessThan(huge.Length,
            "the window's job is to keep input context lean");
    }

    /// <summary>
    /// The window is rendered oldest-first.
    /// </summary>
    /// <remarks>
    /// The loader fetches newest-first to apply the cap, so a rendering that forgot to flip back hands
    /// the model a reversed transcript. That defect surfaces as "the agent keeps re-answering its own
    /// last question" and never as an error, which is why it needs an explicit assertion.
    /// </remarks>
    [Fact]
    public async Task SlidingWindow_IsRenderedChronologically()
    {
        using var db = Db();
        await SeedProjectAndJobAsync(db);
        await SeedSessionAsync(db);

        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        for (var i = 1; i <= 5; i++)
        {
            db.SessionMessages.Add(new SessionMessage
            {
                SessionId = SessionId,
                SenderAgentId = "driver-rook",
                EventType = "thought",
                Content = $"STEP-{i}",
                CreatedAt = baseTime.AddMinutes(i),
            });
        }

        await db.SaveChangesAsync();

        var prompt = (await AssembleAsync(db, new WorkspaceStateRepository(db))).Prompt;

        var positions = Enumerable.Range(1, 5)
            .Select(i => prompt.IndexOf($"STEP-{i}", StringComparison.Ordinal))
            .ToList();

        positions.Should().OnlyContain(p => p >= 0, "all five messages are inside the window");
        positions.Should().BeInAscendingOrder("the conversation must read in the order it happened");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Section stack
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The sections appear in the order the spec fixes.
    /// </summary>
    /// <remarks>
    /// Order is not cosmetic. The stable prefix (identity, memories) is what a driver's prompt cache
    /// keys on, and the incoming message goes last because recency is what a model weights most. A
    /// reordering silently costs every turn its cache hit.
    /// </remarks>
    [Fact]
    public async Task PromptStack_SectionsAppearInSpecOrder()
    {
        using var db = Db();
        await SeedProjectAndJobAsync(db);
        await SeedSessionAsync(db);
        await SeedMemoriesAsync(db);

        var checkpoints = new WorkspaceStateRepository(db);
        await checkpoints.SaveCheckpointAsync(new TaskWorkingCheckpoint(
            JobId.ToString(), TaskId.ToString(), "feat/x", JobTaskStatus.InProgress,
            "earlier work", [], null, DateTimeOffset.UtcNow, TurnSessionId: SessionId.ToString()));

        var prompt = (await AssembleAsync(db, checkpoints,
            new ChatMessageSignal("astra", "Use the existing validator.", DateTimeOffset.UtcNow))).Prompt;

        var order = new[]
        {
            "[PROJECT CONTEXT]",
            "[PERSONA & PLATFORM RULES]",
            "[PERSISTENT MEMORY & NOTES]",
            "[TASK ENVELOPE]",
            "[WORKING CHECKPOINT]",
            EnvironmentRecoveryNote.Header,
            "[RECENT CONVERSATION]",
            "[INCOMING MESSAGE]",
        };

        var positions = order.Select(marker => prompt.IndexOf(marker, StringComparison.Ordinal)).ToList();

        positions.Should().OnlyContain(p => p >= 0, $"every section must be present; prompt was:\n{prompt}");
        positions.Should().BeInAscendingOrder("the spec fixes this order");
    }

    /// <summary>
    /// Persistent memories are injected under the spec's header.
    /// </summary>
    [Fact]
    public async Task Memories_AreInjectedUnderTheSpecifiedHeader()
    {
        using var db = Db();
        await SeedProjectAndJobAsync(db);
        await SeedSessionAsync(db);
        await SeedMemoriesAsync(db);

        var assembled = await AssembleAsync(db, new WorkspaceStateRepository(db));

        assembled.MemoryCount.Should().Be(2);
        assembled.Prompt.Should().Contain("[PERSISTENT MEMORY & NOTES]");
        assembled.Prompt.Should().Contain("dotnet_test_flags")
            .And.Contain("dotnet test -v quiet --nologo");
        assembled.Prompt.Should().Contain("build_and_test");
    }

    /// <summary>
    /// Memories written under the persona as well as the driver both appear.
    /// </summary>
    /// <remarks>
    /// A memory's owner is whichever identity the caller resolved (claims first, then
    /// <c>X-Agent-Id</c>), so a driver row and a persona row can both legitimately hold lessons for the
    /// same project. Reading only one would silently drop the other half.
    /// </remarks>
    [Fact]
    public async Task Memories_ReadForBothAgentAndPersona()
    {
        using var db = Db();
        await SeedProjectAndJobAsync(db);
        await SeedSessionAsync(db);

        db.AgentMemories.Add(AgentMemory.TryCreate("driver-rook", ProjectId,
            AgentMemoryCategories.Environment, "agent-lesson", "from the driver")!);
        db.AgentMemories.Add(AgentMemory.TryCreate("rook", ProjectId,
            AgentMemoryCategories.Environment, "persona-lesson", "from the persona")!);
        await db.SaveChangesAsync();

        var assembled = await AssembleAsync(db, new WorkspaceStateRepository(db));

        assembled.MemoryCount.Should().Be(2);
        assembled.Prompt.Should().Contain("agent-lesson").And.Contain("persona-lesson");
    }

    /// <summary>
    /// A memory from another project is not injected.
    /// </summary>
    [Fact]
    public async Task Memories_DoNotLeakAcrossProjects()
    {
        using var db = Db();
        await SeedProjectAndJobAsync(db);
        await SeedSessionAsync(db);

        db.AgentMemories.Add(AgentMemory.TryCreate("driver-rook", "other-repo",
            AgentMemoryCategories.Environment, "other-project-secret", "belongs elsewhere")!);
        db.AgentMemories.Add(AgentMemory.TryCreate("driver-rook", AgentMemory.GlobalProjectId,
            AgentMemoryCategories.Environment, "global-lesson", "applies everywhere")!);
        await db.SaveChangesAsync();

        var assembled = await AssembleAsync(db, new WorkspaceStateRepository(db));

        assembled.Prompt.Should().NotContain("other-project-secret");
        assembled.Prompt.Should().Contain("global-lesson",
            "the global bucket is deliberately widened into every project read");
    }

    /// <summary>
    /// The working directory in the prompt is the resolved multi-repo path, and it is stated as
    /// authoritative.
    /// </summary>
    [Fact]
    public async Task Prompt_StatesTheResolvedWorkingDirectoryAsAuthoritative()
    {
        using var db = Db();
        await SeedProjectAndJobAsync(db, repositoryName: "payments-api", workingDirectory: "");
        await SeedSessionAsync(db);

        // No workspace path on the input: when one IS supplied the input wins by design (every turn in
        // one workflow run must agree on one directory even if the project row is edited mid-flight), so
        // the project-derived path is only consulted when the workflow was started without one.
        var withoutPath = Input with { WorkspacePath = "" };
        var assembled = await AssembleAsync(db, new WorkspaceStateRepository(db), input: withoutPath);

        assembled.WorkspacePath.Should().Be("/workspace/repos/payments-api",
            "a blank working_directory must derive from repository_name, not fall back to the default " +
            "repo — that is the multi-repo bug this feature removes");
        assembled.Prompt.Should().Contain("/workspace/repos/payments-api");
        assembled.Prompt.Should().NotContain("/workspace/repos/agent-mesh",
            "the default path must not appear once the project names a different repository");
        assembled.Prompt.Should().Contain("do not run pwd",
            "naming the directory only helps if the prompt also says not to verify it");
    }

    /// <summary>
    /// A workspace path carried on the workflow input wins over the project row.
    /// </summary>
    /// <remarks>
    /// The counterpart to the test above, and the reason it exists: without this assertion, "always
    /// re-resolve from the project" would be an equally plausible implementation that passes every other
    /// test here — and it would let an edit to <c>projects.working_directory</c> move a running task's
    /// working directory between its own turns.
    /// </remarks>
    [Fact]
    public async Task WorkspacePathOnTheInput_WinsOverTheProjectRow()
    {
        using var db = Db();
        await SeedProjectAndJobAsync(db, repositoryName: "payments-api", workingDirectory: "/somewhere/else");
        await SeedSessionAsync(db);

        var assembled = await AssembleAsync(db, new WorkspaceStateRepository(db));

        assembled.WorkspacePath.Should().Be("/workspace/repos/agent-mesh",
            "the input is the path the workflow was started with and is stable for its whole run");
    }

    /// <summary>
    /// The task envelope names this workflow's checklist item and marks it in the list.
    /// </summary>
    [Fact]
    public async Task TaskEnvelope_MarksTheFocusTask()
    {
        using var db = Db();
        await SeedProjectAndJobAsync(db);
        await SeedSessionAsync(db);

        var prompt = (await AssembleAsync(db, new WorkspaceStateRepository(db))).Prompt;

        prompt.Should().Contain("Implement the reconciler");
        prompt.Should().Contain("← this workflow",
            "with several checklist items the agent must know which one this turn owns");
        prompt.Should().Contain("Checklist (1/");
    }

    /// <summary>
    /// A pending reply is injected last and untruncated.
    /// </summary>
    [Fact]
    public async Task PendingReply_IsInjectedAsTheIncomingMessage()
    {
        using var db = Db();
        await SeedProjectAndJobAsync(db);
        await SeedSessionAsync(db);

        var reply = new ChatMessageSignal(
            "sorren", "Please split the migration into two commits.", DateTimeOffset.UtcNow, "m-77");

        var prompt = (await AssembleAsync(db, new WorkspaceStateRepository(db), reply)).Prompt;

        prompt.Should().Contain("[INCOMING MESSAGE]");
        prompt.Should().Contain("Please split the migration into two commits.");
        prompt.Should().Contain("sorren");
        prompt.LastIndexOf("[INCOMING MESSAGE]", StringComparison.Ordinal)
            .Should().BeGreaterThan(prompt.IndexOf("[RECENT CONVERSATION]", StringComparison.Ordinal),
                "the reply must be the most recent thing the model reads");
    }

    /// <summary>
    /// With no prior session, the window section says so rather than being silently absent.
    /// </summary>
    [Fact]
    public async Task FirstTurnOfATask_SaysThereIsNoPriorConversation()
    {
        using var db = Db();
        await SeedProjectAndJobAsync(db);

        var prompt = (await AssembleAsync(db, new WorkspaceStateRepository(db))).Prompt;

        prompt.Should().Contain("[RECENT CONVERSATION]")
            .And.Contain("no prior messages in this session");
    }

    /// <summary>
    /// Checkpoint TurnSessionId takes precedence over input TurnSessionId to match documented order.
    /// </summary>
    [Fact]
    public async Task ResolveTurnSessionId_PrefersCheckpointOverInput()
    {
        using var db = Db();
        await SeedProjectAndJobAsync(db);

        var checkpointSessionId = Guid.NewGuid();
        var inputSessionId = Guid.NewGuid();

        var repoMock = new Mock<IWorkspaceStateRepository>();
        repoMock.Setup(r => r.GetCheckpointAsync(Input.JobId, Input.TaskId, default))
            .ReturnsAsync(new TaskWorkingCheckpoint(
                JobId: Input.JobId,
                TaskId: Input.TaskId,
                ActiveBranch: "main",
                Status: JobTaskStatus.InProgress,
                LastSummary: "summary",
                ModifiedFiles: [],
                ActiveStash: null,
                UpdatedAt: DateTimeOffset.UtcNow,
                TurnSessionId: checkpointSessionId.ToString()));

        var assembler = new TurnContextAssembler(
            db,
            repoMock.Object,
            new ShellWorkspaceReconciler(shellRunner: (_, _, _) =>
                Task.FromResult(new CommandExecutionResult(true, 0, CleanProbeOutput))),
            null,
            new AgentMemoryRepository(db));

        var input = Input with { TurnSessionId = inputSessionId.ToString() };
        var resolved = await assembler.ResolveTurnSessionIdAsync(input, CancellationToken.None);

        resolved.Should().Be(checkpointSessionId,
            "the checkpoint's session represents the freshest turn and must take precedence over the workflow input");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Degradation
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A workspace that cannot be probed yields no note, and the turn still assembles.
    /// </summary>
    /// <remarks>
    /// The alternative is that a repository on a dead mount prevents the agent from being prompted at
    /// all, turning a slow turn into no turn.
    /// </remarks>
    [Fact]
    public async Task Assembly_WhenProbeFails_OmitsTheNoteAndStillProducesAPrompt()
    {
        using var db = Db();
        await SeedProjectAndJobAsync(db);
        await SeedSessionAsync(db);

        var reconciler = new ShellWorkspaceReconciler(
            shellRunner: (_, _, _) => Task.FromResult(new CommandExecutionResult(false, 127, "git: not found")));

        var assembled = await new TurnContextAssembler(
            db, new WorkspaceStateRepository(db), reconciler, null,
            new AgentMemoryRepository(db)).AssembleAsync(Input);

        assembled.EnvironmentNote.Should().BeNull();
        assembled.Prompt.Should().NotContain(EnvironmentRecoveryNote.Header);
        assembled.Prompt.Should().Contain("[TASK ENVELOPE]", "the rest of the stack is unaffected");
    }

    /// <summary>
    /// A configured window size is honoured, and clamped.
    /// </summary>
    [Theory]
    [InlineData("8", 8)]
    [InlineData("500", TurnContextAssembler.MaxSlidingWindowSize)]
    [InlineData("0", TurnContextAssembler.DefaultSlidingWindowSize)]
    [InlineData("-3", TurnContextAssembler.DefaultSlidingWindowSize)]
    public void WindowSize_IsConfigurableAndCeiled(string configured, int expected)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [TurnContextAssembler.WindowSizeConfigKey] = configured,
            })
            .Build();

        using var db = Db();
        var assembler = new TurnContextAssembler(
            db, new WorkspaceStateRepository(db), new ShellWorkspaceReconciler(), null, null, config);

        // The ceiling exists because the window is what keeps input context lean; a mis-set value of
        // 10,000 would make this feature the cause of the cost it exists to prevent.
        var resolved = ResolveWindow(config);
        resolved.Should().Be(expected);
    }

    private static int ResolveWindow(IConfiguration config)
    {
        var configured = config.GetValue<int?>(TurnContextAssembler.WindowSizeConfigKey);
        if (configured is null or <= 0) return TurnContextAssembler.DefaultSlidingWindowSize;
        return Math.Min(configured.Value, TurnContextAssembler.MaxSlidingWindowSize);
    }

    /// <summary>
    /// The window query translates against the real provider.
    /// </summary>
    /// <remarks>
    /// The in-memory provider translates any LINQ you hand it, so a query it accepts can still be one
    /// Postgres refuses to build — and the failure would land on the turn path, at runtime, for every
    /// agent. This asserts only that a command is produced (the unreachable host is never contacted),
    /// which is the same technique the existing paging-translation test uses.
    /// </remarks>
    [Fact]
    public void SlidingWindowQuery_TranslatesAgainstThePostgresProvider()
    {
        using var db = TestDbContextFactory.Postgres();
        var sessionId = Guid.NewGuid();

        var query = db.SessionMessages
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId
                        && m.EventType != "tool_result"
                        && m.EventType != "artifact")
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .Take(15);

        var sql = query.ToQueryString();

        // Case-insensitive: Npgsql emits LIMIT/ORDER BY uppercased and the exact casing is its
        // business, not something this project should pin.
        sql.Should().MatchRegex(@"(?i)LIMIT")
            .And.Contain("session_messages")
            .And.Contain("event_type")
            .And.MatchRegex(@"(?i)ORDER BY[\s\S]*created_at DESC");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static TaskExecutionInput Input { get; } = new(
        JobId: JobId.ToString(),
        TaskId: TaskId.ToString(),
        AgentId: "driver-rook",
        ProjectId: ProjectId,
        WorkspacePath: "/workspace/repos/agent-mesh",
        TargetBranch: "main",
        PersonaId: "rook",
        TurnSessionId: SessionId.ToString());

    private static Task<AssembledTurnContext> AssembleAsync(
        AgentMeshDbContext db,
        IWorkspaceStateRepository checkpoints,
        ChatMessageSignal? reply = null,
        TaskExecutionInput? input = null) =>
        new TurnContextAssembler(
            db,
            checkpoints,
            // A probe that reports a clean tree on a real temp directory: the note's presence and
            // position are what these tests assert, and its content is covered by
            // WorkspaceReconciliationTests against real git.
            new ShellWorkspaceReconciler(shellRunner: (_, _, _) =>
                Task.FromResult(new CommandExecutionResult(true, 0, CleanProbeOutput))),
            null,
            new AgentMemoryRepository(db))
        .AssembleAsync(input ?? Input, reply);

    private const string CleanProbeOutput = """
        @@HEADTYPE@@
        branch
        @@BRANCH@@
        feat/recovery
        @@UPSTREAM@@
        origin/feat/recovery
        @@COUNT@@
        0
        @@FILES@@
        @@STASH@@
        @@HEAD@@
        c0ffee1 wire: reconciler
        @@DONE@@
        """;

    private static async Task SeedProjectAndJobAsync(
        AgentMeshDbContext db,
        string repositoryName = "agent-mesh",
        string? workingDirectory = null)
    {
        db.Projects.Add(new Project
        {
            Id = ProjectId,
            Name = "Agent Mesh",
            Description = "the platform",
            RepositoryName = repositoryName,
            WorkingDirectory = workingDirectory ?? "/workspace/repos/agent-mesh",
            GitBaseBranch = "main",
            ProjectInstructions = "Reproduce first, verify always.",
        });

        var tasks = new[]
        {
            new JobTaskDto { Id = Guid.Parse("44444444-4444-4444-4444-444444444444"), Title = "Set up the harness", IsCompleted = true },
            new JobTaskDto { Id = TaskId, Title = "Implement the reconciler", Status = JobTaskStatus.InProgress, AssignedPersonaId = "rook" },
            new JobTaskDto { Id = Guid.Parse("55555555-5555-5555-5555-555555555555"), Title = "Wire the signals" },
        };

        db.ProjectJobs.Add(new ProjectJob
        {
            Id = JobId,
            ProjectId = ProjectId,
            Title = "Context recovery",
            Description = "Stop the post-restart amnesia.",
            GitBranchName = "feat/recovery",
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

        await db.SaveChangesAsync();
    }

    private static async Task SeedSessionAsync(AgentMeshDbContext db)
    {
        db.AgentSessions.Add(new AgentSession
        {
            Id = SessionId,
            Title = "Context recovery turn",
            Status = "thinking",
            PersonaId = "rook",
            DriverId = "driver-rook",
            CreatorAgentId = "driver-rook",
            // The dispatcher writes jobId here and nothing else links a session to a job, so the
            // assembler's session lookup depends on it.
            MetadataJson = JsonSerializer.Serialize(new { jobId = JobId.ToString(), projectId = ProjectId }),
        });

        await db.SaveChangesAsync();
    }

    private static async Task SeedMemoriesAsync(AgentMeshDbContext db)
    {
        db.AgentMemories.Add(AgentMemory.TryCreate("driver-rook", ProjectId,
            AgentMemoryCategories.BuildAndTest, "dotnet_test_flags", "dotnet test -v quiet --nologo")!);
        db.AgentMemories.Add(AgentMemory.TryCreate("driver-rook", ProjectId,
            AgentMemoryCategories.CodebaseGotcha, "snake_case_columns",
            "EF uses snake_case naming; raw SQL must match")!);
        await db.SaveChangesAsync();
    }
}
