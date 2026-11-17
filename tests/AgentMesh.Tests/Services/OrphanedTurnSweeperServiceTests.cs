using AgentMesh.Api.Hubs;
using AgentMesh.Api.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Infrastructure.Services;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Services;

/// <summary>
/// The sweep that ends a turn nobody is advancing any more.
/// </summary>
/// <remarks>
/// <para>
/// The incident this service exists for: an <c>agy</c> driver wedged inside its own shell tool, the
/// turn runner's ceiling fired and failed its activity, the workflow completed with
/// <c>Success: false</c>, and <em>nothing wrote a terminal session status</em>. The cockpit kept saying
/// "running a command" for thirteen hours. The worker's ceiling only covers turns a workflow is
/// watching, so this sweeper is the backstop for every other case.
/// </para>
/// <para>
/// The tests therefore concentrate on the two ways a backstop becomes a new outage: firing too early
/// (settling a turn a live agent is still advancing) and firing correctly but silently (the database
/// fixed, the screen still lying). Both are asserted here.
/// </para>
/// </remarks>
public class OrphanedTurnSweeperServiceTests
{
    private readonly AgentMeshDbContext _db = TestDbContextFactory.Create();
    private readonly Mock<IHubContext<AgentMeshHub>> _hub = new();
    private readonly Mock<IHubClients> _clients = new();
    private readonly Mock<IClientProxy> _proxy = new();
    private readonly List<(string Method, object?[] Args)> _broadcasts = new();

    public OrphanedTurnSweeperServiceTests()
    {
        var services = new ServiceCollection();
        // Singleton on purpose. The sweeper resolves the context from a scope it owns, and a scoped
        // registration makes that scope the owner: disposing it disposes the test's context, so every
        // assertion after the first sweep reads a disposed instance. In production the container owns
        // the per-request context, which is the arrangement this stands in for.
        services.AddSingleton(_db);
        Services = services.BuildServiceProvider();

        // SignalR's ergonomic SendAsync is an extension method, so it cannot be intercepted. What the
        // extension actually calls is the one interface member, SendCoreAsync, and that is the seam a
        // recording double has to sit on. The payload is the first element of the args array.
        _proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((m, a, _) => _broadcasts.Add((m, a)))
            .Returns(Task.CompletedTask);
        _clients.Setup(c => c.All).Returns(_proxy.Object);
        _clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_proxy.Object);
        _hub.Setup(h => h.Clients).Returns(_clients.Object);
    }

    private IServiceProvider Services { get; }

    private OrphanedTurnSweeperService Create(Dictionary<string, string?>? config = null) =>
        new(Services, _hub.Object,
            new ConfigurationBuilder().AddInMemoryCollection(config ?? new Dictionary<string, string?>()).Build(),
            NullLogger<OrphanedTurnSweeperService>.Instance);

    private AgentSession AddSession(string status, TimeSpan silence, Guid? jobId = null)
    {
        var session = new AgentSession
        {
            Title = "Standardized project setup",
            Status = status,
            UpdatedAt = DateTimeOffset.UtcNow - silence,
            MetadataJson = jobId is null ? "{}" : $$"""{"jobId":"{{jobId}}"}"""
        };
        _db.AgentSessions.Add(session);
        _db.SaveChanges();
        return session;
    }

    private ProjectJob AddJob(ProjectJobStatus status, string? heartbeat = "Executing task 4/5: open the PR")
    {
        var job = new ProjectJob
        {
            ProjectId = "proj-1",
            Title = "Standardized project setup",
            Status = status,
            ProgressHeartbeat = heartbeat
        };
        _db.ProjectJobs.Add(job);
        _db.SaveChanges();
        return job;
    }

    // ── what gets swept ──────────────────────────────────────────────────────

    [Fact]
    public async Task SweepAsync_SettlesSessionsQuieterThanThePolicyWindow()
    {
        var orphan = AddSession("thinking", TurnStallPolicy.OrphanedTurnAfter + TimeSpan.FromMinutes(10));

        var count = await Create().SweepAsync(CancellationToken.None);

        count.Should().Be(1);
        var reloaded = await _db.AgentSessions.AsNoTracking().FirstAsync(s => s.Id == orphan.Id);
        reloaded.Status.Should().Be(TurnStallPolicy.SettledStatus);
    }

    [Fact]
    public async Task SweepAsync_LeavesLiveTurnsAndParkedSessionsAlone()
    {
        var justInsideWindow = AddSession("thinking", TurnStallPolicy.OrphanedTurnAfter - TimeSpan.FromMinutes(10));
        var parked = AddSession("active", TimeSpan.FromDays(3));
        var waitingForHuman = AddSession("waiting_for_input", TimeSpan.FromDays(3));

        var count = await Create().SweepAsync(CancellationToken.None);

        count.Should().Be(0);
        var statuses = await _db.AgentSessions.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Status);
        statuses[justInsideWindow.Id].Should().Be("thinking", "the worker may still be observing this turn");
        statuses[parked.Id].Should().Be("active", "a session between turns is not an abandoned turn");
        statuses[waitingForHuman.Id].Should().Be("waiting_for_input", "a human is deliberately holding this one");
    }

    [Fact]
    public async Task SweepAsync_WritesWhyIntoTheTranscript()
    {
        var orphan = AddSession("busy", TurnStallPolicy.OrphanedTurnAfter + TimeSpan.FromMinutes(5));

        await Create().SweepAsync(CancellationToken.None);

        var rows = await _db.SessionMessages.Where(m => m.SessionId == orphan.Id).ToListAsync();
        rows.Should().ContainSingle()
            .Which.Content.Should().StartWith(TurnStallPolicy.SettleNoticePrefix);
        rows.Single().SenderAgentId.Should().Be(OrphanedTurnSweeperService.SweeperAgentId);
    }

    [Fact]
    public async Task SweepAsync_IsIdempotent_AndDoesNotReNotify()
    {
        AddSession("thinking", TurnStallPolicy.OrphanedTurnAfter + TimeSpan.FromMinutes(5));

        var service = Create();
        var first = await service.SweepAsync(CancellationToken.None);
        var second = await service.SweepAsync(CancellationToken.None);

        // A settled session is no longer a live status, so it drops out of the candidate set. If it did
        // not, every five-minute cycle would append another notice row to the same transcript.
        first.Should().Be(1);
        second.Should().Be(0);
        (await _db.SessionMessages.CountAsync()).Should().Be(1);
    }

    // ── the job has to say the same thing ────────────────────────────────────

    [Fact]
    public async Task SweepAsync_MarksTheOwningJobStalled()
    {
        var job = AddJob(ProjectJobStatus.Active);
        AddSession("thinking", TurnStallPolicy.OrphanedTurnAfter + TimeSpan.FromMinutes(5), job.Id);

        await Create().SweepAsync(CancellationToken.None);

        // Without this the cockpit shows an Active job with no recovery affordance while its only turn is
        // dead in the database, which is the exact lie the sweep is supposed to end.
        var reloaded = await _db.ProjectJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
        TurnStallSettler.IsStalledHeartbeat(reloaded.ProgressHeartbeat).Should().BeTrue();
    }

    [Fact]
    public async Task SweepAsync_DoesNotStallAJobThatIsNotActive()
    {
        var job = AddJob(ProjectJobStatus.Completed, heartbeat: "Executing task 5/5");
        AddSession("thinking", TurnStallPolicy.OrphanedTurnAfter + TimeSpan.FromMinutes(5), job.Id);

        await Create().SweepAsync(CancellationToken.None);

        var reloaded = await _db.ProjectJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
        reloaded.ProgressHeartbeat.Should().Be("Executing task 5/5");
    }

    [Fact]
    public async Task SweepAsync_SweepsAPlainChatSession_WithoutTouchingAnyJob()
    {
        var job = AddJob(ProjectJobStatus.Active);
        AddSession("thinking", TurnStallPolicy.OrphanedTurnAfter + TimeSpan.FromMinutes(5));

        var count = await Create().SweepAsync(CancellationToken.None);

        // A chat session with no jobId has no job to mark. Attributing it to the only job in the system
        // would put a false badge on an unrelated job.
        count.Should().Be(1);
        (await _db.ProjectJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id)).ProgressHeartbeat
            .Should().Be("Executing task 4/5: open the PR");
    }

    // ── the screen has to change too ─────────────────────────────────────────

    [Fact]
    public async Task SweepAsync_BroadcastsOneSessionUpdated_PerSettledSession()
    {
        var a = AddSession("thinking", TurnStallPolicy.OrphanedTurnAfter + TimeSpan.FromMinutes(5));
        var b = AddSession("busy", TurnStallPolicy.OrphanedTurnAfter + TimeSpan.FromMinutes(5));

        await Create().SweepAsync(CancellationToken.None);

        // The API owns the hub. Settling without broadcasting leaves the indicator up until a manual
        // refresh — correct in the database, wrong on the screen.
        var ids = _broadcasts.Where(b => b.Method == "SessionUpdated")
            .Select(b => ((AgentSession)b.Args[0]!).Id)
            .ToList();
        ids.Should().BeEquivalentTo(new[] { a.Id, b.Id });
    }

    [Fact]
    public async Task BroadcastsStopAfterTheWrites_SoTheCockpitNeverHearsAboutAnUncommittedSettle()
    {
        var orphan = AddSession("thinking", TurnStallPolicy.OrphanedTurnAfter + TimeSpan.FromMinutes(5));
        var statusSeenByTheBroadcast = "";

        _proxy.Setup(p => p.SendCoreAsync("SessionUpdated", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                // Read the store as a client would see it at the instant the hub call is made. If the
                // broadcast went out before the write was committed, a cockpit refreshing right then
                // would read "thinking" and repaint the very indicator this sweep exists to clear.
                statusSeenByTheBroadcast = _db.AgentSessions.AsNoTracking()
                    .First(s => s.Id == orphan.Id).Status;
            })
            .Returns(Task.CompletedTask);

        await Create().SweepAsync(CancellationToken.None);

        statusSeenByTheBroadcast.Should().Be(TurnStallPolicy.SettledStatus);
    }

    [Fact]
    public async Task SweepAsync_StillSettles_WhenNoHubIsConfigured()
    {
        var orphan = AddSession("thinking", TurnStallPolicy.OrphanedTurnAfter + TimeSpan.FromMinutes(5));
        var hubless = new OrphanedTurnSweeperService(
            Services, null, new ConfigurationBuilder().Build(), NullLogger<OrphanedTurnSweeperService>.Instance);

        var count = await hubless.SweepAsync(CancellationToken.None);

        count.Should().Be(1);
        (await _db.AgentSessions.AsNoTracking().FirstAsync(s => s.Id == orphan.Id))
            .Status.Should().Be(TurnStallPolicy.SettledStatus);
    }

    // ── configuration must not be able to break the ladder ───────────────────

    [Fact]
    public async Task SweepAsync_IgnoresAThresholdBelowThePolicyFloor()
    {
        AddSession("thinking", TimeSpan.FromMinutes(60));
        var service = Create(new Dictionary<string, string?> { ["TurnReconciler:OrphanMinutes"] = "1" });

        var count = await service.SweepAsync(CancellationToken.None);

        // The runner's own ceiling is 110 minutes. A sweeper tuned to a minute would stamp "stopped"
        // over agents mid-command across the fleet on a five-minute cycle, so a smaller number is
        // ignored rather than honoured.
        count.Should().Be(0);
        (await _db.AgentSessions.AsNoTracking().ToListAsync()).Single().Status.Should().Be("thinking");
    }

    [Fact]
    public async Task SweepAsync_HonoursALargerConfiguredThreshold()
    {
        AddSession("thinking", TimeSpan.FromMinutes(150));
        var service = Create(new Dictionary<string, string?> { ["TurnReconciler:OrphanMinutes"] = "200" });

        var count = await service.SweepAsync(CancellationToken.None);

        // Raising the window is safe (it only delays the sweep) and is the knob an operator gets for a
        // fleet with legitimately long turns.
        count.Should().Be(0);
    }

    [Fact]
    public async Task SweepAsync_SweepsWhenTheConfiguredThresholdIsEventuallyCrossed()
    {
        AddSession("thinking", TimeSpan.FromMinutes(210));
        var service = Create(new Dictionary<string, string?> { ["TurnReconciler:OrphanMinutes"] = "200" });

        (await service.SweepAsync(CancellationToken.None)).Should().Be(1);
    }

    // ── loop lifecycle ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SweepsOnStart_AndStopsCleanly()
    {
        var orphan = AddSession("thinking", TurnStallPolicy.OrphanedTurnAfter + TimeSpan.FromMinutes(5));
        var service = Create(new Dictionary<string, string?> { ["TurnReconciler:CheckIntervalSeconds"] = "3600" });
        using var cts = new CancellationTokenSource();

        var running = service.StartAsync(cts.Token);

        await WaitUntil(async () => (await _db.AgentSessions.AsNoTracking().FirstAsync(s => s.Id == orphan.Id))
            .Status == TurnStallPolicy.SettledStatus);

        // A one-hour interval proves the first sweep does not wait for a timer to elapse: an operator
        // restarting the API should not have to wait five minutes for the lie to leave the screen.
        await service.StopAsync(CancellationToken.None);
        await running;
        cts.Cancel();

        _broadcasts.Should().ContainSingle(b => b.Method == "SessionUpdated",
            "stopping the service must not sweep a second time on the way out");
    }

    private static async Task WaitUntil(Func<Task<bool>> condition, int timeoutMs = 5000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("Condition was not met within " + timeoutMs + "ms");
    }
}
