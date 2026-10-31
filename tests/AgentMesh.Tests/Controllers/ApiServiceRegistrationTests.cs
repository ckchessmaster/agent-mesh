using AgentMesh.Api.Controllers;
using AgentMesh.Api.Services;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StackExchange.Redis;
using Temporalio.Client;
using Xunit;
using AgentMesh.Core.Workflows;
using ProjectModel = AgentMesh.Core.Models.Project;
using ProjectJobModel = AgentMesh.Core.Models.ProjectJob;

namespace AgentMesh.Tests.Controllers;

/// <summary>
/// The API's real service container, asked to activate the macro-orchestration controllers.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this file exists at all.</strong> Every other test in this suite constructs its
/// controller with <c>new MacroTurnsController(db, router)</c>. That proves the class works given a
/// router; it cannot prove the application <em>has</em> a router to give. The registration lives in
/// <c>Program.cs</c>, and <c>Program.cs</c> is the one file in this feature with no test of any kind
/// against it — so the entire orchestration surface could be wired to nothing and the suite would stay
/// green. It did, in fact, ship that way: this test was written against the un-registered container and
/// failed before the one-line registration was added.
/// </para>
/// <para>
/// <strong>The asymmetry that makes a plain "does it resolve" check insufficient.</strong> The two
/// consumers of <see cref="IWorkflowSignalRouter"/> fail in opposite directions when it is missing:
/// <list type="bullet">
/// <item><description>
/// <c>MacroTurnsController</c> declares it as a <em>required</em> constructor parameter, so activation
/// throws <c>InvalidOperationException: Unable to resolve service for type
/// 'AgentMesh.Api.Services.IWorkflowSignalRouter'</c>. Loud, and it 500s every start/status/review
/// request — though only for authenticated callers, since authorization runs before controller
/// activation and an anonymous request gets a 401 without the container ever being consulted.
/// </description></item>
/// <item><description>
/// <c>ProjectJobsController</c> declares it as <em>optional</em> (<c>= null</c>), because ~40 existing
/// controller tests construct that type directly and must not be forced to know about Temporal. DI
/// happily injects null, <c>RouteReplyToWorkflowAsync</c> opens with <c>if (workflows is null) return;</c>,
/// and Job Chat replies are then persisted, broadcast, and silently never routed. The workflow sits out
/// its seven-day park while a human answers in a window nothing is reading — which is a worse failure
/// than the busy-polling it replaced, because polling at least eventually noticed.
/// </description></item>
/// </list>
/// So the second case is asserted explicitly: not "did it resolve" but "is the injected instance
/// non-null". A test that only checked the required parameter would pass while the reply-routing feature
/// was entirely dead.
/// </para>
/// <para>
/// <strong>Why the optional parameter is kept rather than made required.</strong> Making it required
/// would mean editing ~40 unrelated test files to satisfy a dependency they do not exercise, and it would
/// couple every <c>PostJobMessage</c> test to Temporal. The null default is a deliberate seam; what was
/// missing is the guarantee that the <em>production</em> container does not use it.
/// </para>
/// </remarks>
public class ApiServiceRegistrationTests : IClassFixture<WebApplicationFactory<AgentMesh.Api.Program>>
{
    private readonly WebApplicationFactory<AgentMesh.Api.Program> _factory;
    /// <summary>
    /// The descriptor list the host was built from, captured from its configuration callback.
    /// </summary>
    /// <remarks>
    /// Held rather than re-read because <c>IServiceProvider</c> exposes no lifetimes: <c>GetService</c>
    /// answers "can I have one" but not "at what lifetime", and the extension methods that would enumerate
    /// descriptors bind to the wrong overload against a provider. The collection handed to
    /// <c>ConfigureServices</c> is the same instance the host builds from. Not <c>readonly</c>: it is
    /// assigned from inside the configuration lambda, which the compiler does not treat as constructor
    /// flow.
    /// </remarks>
    private IServiceCollection? _capturedServices;

    public ApiServiceRegistrationTests(WebApplicationFactory<AgentMesh.Api.Program> fixture)
    {
        // The substitutions are the same ones StatusEndpointTests uses: the container must be buildable
        // without Postgres, Redis, S3, or a Temporal server, because what is under test is which
        // implementations are *registered*, not whether the infrastructure is reachable.
        _factory = BuildConfigured(fixture, dbName: null, extra: null, capture: s => _capturedServices = s);
    }

    /// <summary>
    /// Builds a host whose <c>Program.cs</c> registrations are followed by the in-process substitutions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Callers that need to swap a registration pass <paramref name="extra"/> and must derive from a
    /// <strong>pristine</strong> <see cref="WebApplicationFactory{TEntryPoint}"/>, never from an already
    /// configured one. Deriving twice compounds the <c>ConfigureServices</c> callbacks: the second pass
    /// re-adds a DbContext provider on top of the first, and EF then refuses to build the scope with
    /// "Services for database providers 'Npgsql…', 'Microsoft…InMemory' have been registered". Each host
    /// is therefore configured exactly once.
    /// </para>
    /// </remarks>
    private static WebApplicationFactory<AgentMesh.Api.Program> BuildConfigured(
        WebApplicationFactory<AgentMesh.Api.Program> source,
        string? dbName,
        Action<IServiceCollection>? extra,
        Action<IServiceCollection>? capture) =>
        source.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                SubstituteInfrastructure(services, dbName ?? "ApiServiceRegistrationTestDb_" + Guid.NewGuid());
                extra?.Invoke(services);
                capture?.Invoke(services);
            });
        });

    /// <summary>
    /// A host built from scratch, for tests that replace a production registration.
    /// </summary>
    /// <remarks>
    /// The shared xUnit fixture is deliberately left untouched: the descriptor-reading tests must observe
    /// the real registration set, and mutating the fixture would leak a fake router into them depending
    /// on execution order.
    /// </remarks>
    private static WebApplicationFactory<AgentMesh.Api.Program> BuildWith(
        Action<IServiceCollection> extra) =>
        BuildConfigured(new WebApplicationFactory<AgentMesh.Api.Program>(), dbName: null, extra, capture: null);

    /// <summary>
    /// Replaces the app's real infrastructure with in-process fakes.
    /// </summary>
    /// <remarks>
    /// Runs after <c>Program.cs</c>'s own registrations, which is what makes the removal step
    /// meaningful: the descriptors it deletes are the ones <c>AddNpgsqlDbContext</c> added.
    /// </remarks>
    private static void SubstituteInfrastructure(IServiceCollection services, string dbName)
    {
        // Everything Entity Framework registered, not merely the descriptors a caller would think to
        // name. AddNpgsqlDbContext (Aspire) registers the provider's internal services under
        // Microsoft.EntityFrameworkCore.* service types with Npgsql implementations; removing only
        // DbContextOptions/AgentMeshDbContext/Npgsql-namespaced types leaves those behind, AddDbContext
        // then adds the InMemory set on top, and EF refuses to build the scope with "Services for
        // database providers 'Npgsql…', 'Microsoft…InMemory' have been registered". StatusEndpointTests
        // gets away with the narrow filter because it never resolves the DbContext; this class does.
        //
        // Clearing the lot and letting the AddDbContext call below rebuild it for InMemory is the only
        // version that is correct regardless of which provider the app configured.
        var efDescriptors = services.Where(d =>
            d.ServiceType == typeof(DbContextOptions<AgentMeshDbContext>) ||
            d.ServiceType == typeof(DbContextOptions) ||
            d.ServiceType == typeof(AgentMeshDbContext) ||
            d.ServiceType.FullName?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) == true ||
            d.ServiceType.Namespace?.StartsWith("Npgsql") == true ||
            d.ImplementationType?.Namespace?.StartsWith("Npgsql") == true).ToList();
        foreach (var d in efDescriptors)
        {
            services.Remove(d);
        }

        services.AddDbContext<AgentMeshDbContext>(opts => opts.UseInMemoryDatabase(dbName));

        var mockStorage = new Mock<IStorageService>();
        mockStorage.Setup(s => s.EnsureBucketExistsAsync(default)).Returns(Task.CompletedTask);
        services.AddSingleton(mockStorage.Object);

        var mockRedis = new Mock<IConnectionMultiplexer>();
        var mockDb = new Mock<IDatabase>();
        mockRedis.Setup(r => r.IsConnected).Returns(true);
        mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDb.Object);
        mockDb.Setup(d => d.PingAsync(It.IsAny<CommandFlags>())).ReturnsAsync(TimeSpan.FromMilliseconds(2));
        services.AddSingleton(mockRedis.Object);

        services.AddSingleton(new Mock<ITemporalClient>().Object);
    }

    /// <summary>Forces the host to build, then returns the descriptor list it was built from.</summary>
    private IServiceCollection Descriptors
    {
        get
        {
            _ = _factory.Services;
            return _capturedServices ?? throw new InvalidOperationException(
                "the factory never invoked its ConfigureServices callback");
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // The registration itself
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>IWorkflowSignalRouter</c> is registered against the real implementation.
    /// </summary>
    /// <remarks>
    /// Asserted on the descriptor rather than on a resolved instance so the failure message names the
    /// missing line in <c>Program.cs</c> instead of an exception from three layers of DI internals.
    /// </remarks>
    [Fact]
    public void WorkflowSignalRouter_IsRegisteredInServiceCollection()
    {
        var descriptor = Descriptors.FirstOrDefault(d => d.ServiceType == typeof(IWorkflowSignalRouter));

        descriptor.Should().NotBeNull(
            "Program.cs must register IWorkflowSignalRouter. Without it MacroTurnsController 500s on " +
            "every request and ProjectJobsController silently stops routing chat replies into the " +
            "parked workflow — see the class remarks for why the second failure is the dangerous one.");

        descriptor!.ImplementationType.Should().Be<WorkflowSignalRouter>();
    }

    /// <summary>
    /// Scoped, not singleton.
    /// </summary>
    /// <remarks>
    /// The router resolves an <see cref="ITemporalClient"/> per call and caches nothing, so a singleton
    /// would work in practice — but it is registered Scoped to match the lifetime of both consumers, and
    /// pinning the lifetime here is what stops a future "optimization" from capturing a scoped DbContext
    /// into a singleton graph and producing the cross-request-state bug this project has already been
    /// bitten by once.
    /// </remarks>
    [Fact]
    public void WorkflowSignalRouter_IsScopedLikeItsConsumers()
    {
        var descriptor = Descriptors.Single(d => d.ServiceType == typeof(IWorkflowSignalRouter));

        descriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    /// <summary>
    /// It resolves through a real request scope, not just as a descriptor.
    /// </summary>
    /// <remarks>
    /// The distinction catches a different mistake: a type can be registered and still be
    /// unresolvable because one of <em>its own</em> constructor parameters is missing.
    /// <c>WorkflowSignalRouter</c> takes <c>ITemporalClient</c> (registered by
    /// <c>AddTemporalClient</c>) and an optional logger, so this is the check that <c>AddTemporalClient</c>
    /// is still above it in <c>Program.cs</c>.
    /// </remarks>
    [Fact]
    public void WorkflowSignalRouter_ResolvesFromARequestScope()
    {
        using var scope = _factory.Services.CreateScope();

        var router = scope.ServiceProvider.GetService<IWorkflowSignalRouter>();

        router.Should().BeOfType<WorkflowSignalRouter>();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Controller activation
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>MacroTurnsController</c> can be activated by the container.
    /// </summary>
    /// <remarks>
    /// This is the exact operation that throws when the registration is absent, because the router is a
    /// required parameter. Activating the controller type directly is a more faithful reproduction than
    /// calling the endpoint: it is independent of authentication, which otherwise short-circuits with a
    /// 401 before the container is ever consulted and would make this test pass for the wrong reason.
    /// </remarks>
    [Fact]
    public void MacroTurnsController_ActivatesFromTheRealContainer()
    {
        using var scope = _factory.Services.CreateScope();

        var controller = ActivatorUtilities.CreateInstance<MacroTurnsController>(scope.ServiceProvider);

        controller.Should().NotBeNull();
    }

    /// <summary>
    /// <c>MacroTurnController</c> can be activated by the container.
    /// </summary>
    /// <remarks>
    /// Included because it takes <c>JobTurnDispatcher</c> as a required parameter and
    /// <c>IJobWorkspaceService</c> as an optional one — the same mixed pattern, on the dispatch half of
    /// the feature. If either registration moves, this fails rather than the first POST discovering it.
    /// </remarks>
    [Fact]
    public void MacroTurnController_ActivatesFromTheRealContainer()
    {
        using var scope = _factory.Services.CreateScope();

        var controller = ActivatorUtilities.CreateInstance<MacroTurnController>(scope.ServiceProvider);

        controller.Should().NotBeNull();
    }

    /// <summary>
    /// A <c>ProjectJobsController</c> activated by a container that registers the router actually routes
    /// a human reply.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What this proves and what it does not. It proves the controller's optional parameter is genuinely
    /// consumed: given a container that can supply a router, the container-activated controller calls it
    /// exactly once with the reply. It does <strong>not</strong> prove production supplies one — it
    /// substitutes a recording fake, so the production registration is pinned by
    /// <see cref="WorkflowSignalRouter_IsRegisteredInServiceCollection"/> and
    /// <see cref="MacroTurnsController_ActivatesFromTheRealContainer"/> instead. Read together the two
    /// close the gap: production registers the service, and a controller built from a container that
    /// registers the service routes.
    /// </para>
    /// <para>
    /// Asserted behaviourally rather than by reflecting on the primary constructor's captured parameter
    /// because that field's name is compiler-generated and varies between toolchains
    /// (<c>&lt;workflows&gt;5__2</c> on one, a closure display class on another). A reflection assertion
    /// would also pass for the wrong reason: "a field exists and holds an object" is not the claim under
    /// test. "A reply posted through the container-produced controller reaches the router" is.
    /// </para>
    /// <para>
    /// The fake is necessary because the real router cannot be observed through a mocked
    /// <see cref="ITemporalClient"/>: <c>GetWorkflowHandle</c> returns null on a loose mock, the router
    /// throws, and <c>RouteReplyToWorkflowAsync</c> swallows it into a warning — indistinguishable from
    /// the never-called case without a recording double.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ProjectJobsController_ActivatedByTheRealContainer_RoutesAHumanReplyToTheWorkflow()
    {
        var recorder = new RecordingRouter();

        using var factory = BuildWith(services =>
        {
            // Replaced, not merely added: the point is that the *production* registration is what routes,
            // so the fake stands in for it. SingleOrDefault rather than Single so that a missing
            // registration surfaces as the assertion below with its explanation, not as a bare LINQ
            // "sequence contains no matching element" from inside a host-builder callback.
            var production = services.SingleOrDefault(d => d.ServiceType == typeof(IWorkflowSignalRouter));
            if (production is not null) services.Remove(production);
            services.AddScoped<IWorkflowSignalRouter>(_ => recorder);
        });

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentMeshDbContext>();

        var projectId = "app-" + Guid.NewGuid().ToString("N")[..8];
        var jobId = Guid.NewGuid();

        // Deliberately no personas assigned and no default lead: with nothing to mention, the endpoint
        // skips its own turn-dispatch branch and falls through to the routing call, which is the only
        // thing under test. Seeding a persona here would make the test pass through the dispatcher
        // instead of through the router.
        db.Projects.Add(new ProjectModel { Id = projectId, Name = "App" });
        db.ProjectJobs.Add(new ProjectJobModel
        {
            Id = jobId,
            ProjectId = projectId,
            Title = "Orchestrated task",
            TasksJson = "[]",
        });
        await db.SaveChangesAsync();

        var controller = ActivatorUtilities.CreateInstance<ProjectJobsController>(scope.ServiceProvider);

        var result = await controller.PostJobMessage(
            jobId,
            new PostJobMessageRequest(Content: "Use OIDC, not passwords.", SenderType: "user"),
            CancellationToken.None);

        result.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>(
            "the message must still be accepted when routing is unavailable — a Temporal outage may not " +
            "turn into 'your message was not saved'");

        recorder.ChatSends.Should().HaveCount(1,
            "a human reply on an orchestrated job must reach the parked workflow; zero calls means the " +
            "router was never injected, which is the exact shape of the missing-registration bug");
        recorder.ChatSends[0].jobId.Should().Be(jobId);
        recorder.ChatSends[0].signal.Content.Should().Be("Use OIDC, not passwords.");
        recorder.ChatSends[0].signal.SenderId.Should().Be("operator");
    }

    /// <summary>
    /// A router that records instead of signalling, so the assertion is about whether the application
    /// wired anything up rather than about Temporal's behaviour.
    /// </summary>
    private sealed class RecordingRouter : IWorkflowSignalRouter
    {
        public List<(Guid jobId, ChatMessageSignal signal)> ChatSends { get; } = [];

        public List<(Guid jobId, PrReviewSignal signal)> ReviewSends { get; } = [];

        public Task<SignalDelivery> SendChatMessageAsync(
            Guid jobId,
            ChatMessageSignal signal,
            CancellationToken ct = default)
        {
            ChatSends.Add((jobId, signal));
            return Task.FromResult(new SignalDelivery(
                WorkflowSignalRouter.WorkflowIdFor(jobId), SignalDeliveryStatus.Delivered));
        }

        public Task<SignalDelivery> SendPrReviewAsync(
            Guid jobId,
            PrReviewSignal signal,
            CancellationToken ct = default)
        {
            ReviewSends.Add((jobId, signal));
            return Task.FromResult(new SignalDelivery(
                WorkflowSignalRouter.WorkflowIdFor(jobId), SignalDeliveryStatus.Delivered));
        }

        public Task<WorkflowStartResult> StartTaskWorkflowAsync(
            TaskExecutionInput input,
            CancellationToken ct = default) =>
            Task.FromResult(new WorkflowStartResult(true, "test-workflow"));

        public Task<TaskStatusView?> GetStatusAsync(
            Guid jobId,
            string? taskId,
            CancellationToken ct = default) =>
            Task.FromResult<TaskStatusView?>(null);

        /// <summary>
        /// Reports "not orchestrated" unless a test opts in.
        /// </summary>
        /// <remarks>
        /// False by default keeps this fake a faithful stand-in for pre-orchestration behaviour, which is
        /// what the routing test needs: PostJobMessage must keep taking its normal path so the assertion
        /// isolates the routing call rather than a suppressed dispatch.
        /// </remarks>
        public bool WorkflowDriving { get; set; }

        public Task<bool> IsWorkflowDrivingAsync(Guid jobId, string? targetPersonaId, CancellationToken ct = default) =>
            Task.FromResult(WorkflowDriving);
    }
}
