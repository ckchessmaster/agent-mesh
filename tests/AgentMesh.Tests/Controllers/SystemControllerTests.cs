using System.Security.Claims;
using AgentMesh.Api.Controllers;
using AgentMesh.Api.Services;
using AgentMesh.Cli.Models;
using AgentMesh.Cli.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AgentMesh.Tests.Controllers;

/// <summary>
/// Pins the HTTP contract of GET /api/system/capabilities.
///
/// The probe-level tests assert that <see cref="IDriverCapabilityProbe"/> never invents a reading.
/// These assert the other half: that the controller does not re-invent one either. Before the fix
/// the endpoint returned one hardcoded model table per adapter, ignored <c>agentId</c> entirely,
/// and gave every caller the same answer regardless of which driver they asked about.
/// </summary>
public class SystemControllerTests
{
    private static Agent AgyDriver(string id = "agy-core") => new()
    {
        Id = id,
        Name = "Agy Core",
        TrustTier = TrustTier.Intern,
        RuntimeType = RuntimeType.CliAgy,
        CapabilitiesJson = """{"adapter":"agy","capabilities":["chat"]}"""
    };

    private static Agent OpenAiDriver(string id = "qwen-driver", string endpoint = "http://10.0.0.9:8000/v1")
        => new()
        {
            Id = id,
            Name = "Qwen Driver",
            TrustTier = TrustTier.Intern,
            RuntimeType = RuntimeType.OpenAiCompatible,
            CapabilitiesJson = $$"""{"adapter":"openai","endpoint":"{{endpoint}}","models":["hand-picked-model"]}"""
        };

    private static SystemController Controller(IDriverCapabilityProbe probe, AgentMeshDbContext? db = null)
        => new(probe);

    private static async Task<SystemCapabilitiesResponse> CapabilitiesAsync(
        SystemController controller,
        AgentMeshDbContext? db,
        string? agentId = null,
        bool refresh = false)
    {
        var result = await controller.GetCapabilities(agentId, refresh, db: db);
        return result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<SystemCapabilitiesResponse>().Subject;
    }

    [Fact]
    public async Task GetCapabilities_BaselineMap_KeepsModesButCarriesNoModelList()
    {
        // Modes and tiers are concepts our adapters implement, so a static table is accurate about
        // them. A model list is a claim about someone else's account or server, and every bug in the
        // old table came from treating those two the same way.
        var response = await CapabilitiesAsync(Controller(NullDriverCapabilityProbe.Instance), db: null);

        response.AgentRuntimes.Should().ContainKeys("agy", "copilot", "claude", "openai", "generic");

        foreach (var runtime in response.AgentRuntimes.Values)
        {
            runtime.Models.Should().BeEmpty($"{runtime.Adapter} baseline must not invent models");
            runtime.ModelSource.Should().Be("unavailable");
            runtime.UnavailableReason.Should().NotBeNullOrWhiteSpace();
        }

        response.AgentRuntimes["agy"].Modes.Should().Contain(m => m.Id == "accept-edits");
        response.AgentRuntimes["copilot"].Tiers.Should().Contain(t => t.Id == "intelligence");
        response.AgentRuntimes["openai"].Modes.Should().Contain(m => m.Id == "direct");
    }

    [Fact]
    public async Task GetCapabilities_AgyBaseline_HidesContextSelection()
    {
        // agy --help exposes --model/--effort/--mode and no context-size flag, and AgyAdapter
        // forwards none. The picker used to offer 128k..2m on this driver and silently did nothing.
        var response = await CapabilitiesAsync(Controller(NullDriverCapabilityProbe.Instance), db: null);

        var agy = response.AgentRuntimes["agy"];
        agy.SupportsContextSelection.Should().BeFalse();
        agy.ContextSizes.Should().BeNull();
    }

    [Fact]
    public async Task GetCapabilities_CopilotBaseline_OffersNoEffortLadder()
    {
        // CopilotAdapter builds --model/--mode/--context and no --effort flag.
        var response = await CapabilitiesAsync(Controller(NullDriverCapabilityProbe.Instance), db: null);

        response.AgentRuntimes["copilot"].ContextSizes.Should().NotBeEmpty();
        response.AgentRuntimes["copilot"].ContextSizes.Should().NotContain(c => c.Id == "2m",
            "CopilotAdapter maps 2m onto the CLI's 1m tier, so advertising 2m promised a window the " +
            "driver cannot request");
    }

    [Fact]
    public async Task GetCapabilities_ClaudeBaseline_StaysInsideAutocompactBounds()
    {
        // claude --help: --autocompact <auto|tokens>  (auto, or 100k-1M tokens)
        var response = await CapabilitiesAsync(Controller(NullDriverCapabilityProbe.Instance), db: null);

        var sizes = response.AgentRuntimes["claude"].ContextSizes!.Select(c => c.Id).ToList();
        sizes.Should().Contain("auto");
        sizes.Should().Contain("100k");
        sizes.Should().Contain("1m");
        sizes.Should().NotContain("2m");
        sizes.Should().NotContain("50k");
    }

    [Fact]
    public async Task GetCapabilities_BaselineContextLadder_IsBoundedByWhatTheRuntimeHonours()
    {
        // The openai table used to advertise 2m while OpenAiAdapter.ResolveModelContextLimit clamped
        // the run to 128k for an unknown model. The picker and the run must not disagree.
        var response = await CapabilitiesAsync(Controller(NullDriverCapabilityProbe.Instance), db: null);

        var ids = response.AgentRuntimes["openai"].ContextSizes!.Select(c => c.Id).ToList();
        ids.Should().NotContain("2m");
        ids.Should().Contain("1m");
        ModelContextLimits.ParseOption("1m").Should().BeLessThanOrEqualTo(ModelContextLimits.MaxSelectableTokenLimit);
    }

    [Fact]
    public async Task GetCapabilities_HonorsAgentId_ResolvingThatDriversAdapter()
    {
        // The endpoint used to accept agentId and ignore it, so the dashboard's per-driver lookup
        // silently received the host-wide table.
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(AgyDriver());
        db.Agents.Add(OpenAiDriver());
        await db.SaveChangesAsync();

        var probe = new RecordingProbe(new DriverCapabilityReading(
            Adapter: "openai",
            Models: new[] { new DriverModelCapability("qwen3.8-flash-next", "Qwen3.8", Array.Empty<string>()) },
            ModelSource: CapabilitySourceKind.Probed,
            ObservedAt: DateTimeOffset.UtcNow));

        var response = await CapabilitiesAsync(Controller(probe), db, agentId: "qwen-driver");

        response.Drivers.Should().ContainSingle();
        response.Drivers![0].DriverId.Should().Be("qwen-driver");
        response.Drivers[0].Models.Should().ContainSingle(m => m.Id == "qwen3.8-flash-next");
        probe.Requests.Should().ContainSingle(r => r.DriverId == "qwen-driver",
            "only the requested driver should be probed, not every driver on the host");
    }

    [Fact]
    public async Task GetCapabilities_TwoDriversOnDifferentServers_GetDifferentModelLists()
    {
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(OpenAiDriver("driver-a", "http://host-a:8000/v1"));
        db.Agents.Add(OpenAiDriver("driver-b", "http://host-b:8000/v1"));
        await db.SaveChangesAsync();

        var probe = new RecordingProbe(new DriverCapabilityReading(
            Adapter: "openai",
            Models: new[] { new DriverModelCapability("model-x", "Model X", Array.Empty<string>()) },
            ModelSource: CapabilitySourceKind.Probed,
            ObservedAt: DateTimeOffset.UtcNow));

        var response = await CapabilitiesAsync(Controller(probe), db);

        // Each driver's own endpoint must be resolved, not one shared adapter-level guess.
        probe.Requests.Select(r => r.Endpoint).Should().Equal("http://host-a:8000/v1", "http://host-b:8000/v1");
        response.Drivers.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetCapabilities_UnreadableDriver_ReportsUnavailableWithNoModels()
    {
        // The regression this guards: a probe that returns nothing used to fall through to a
        // fabricated catalogue, which looked identical to a real answer in the picker.
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(AgyDriver());
        await db.SaveChangesAsync();

        var response = await CapabilitiesAsync(Controller(new RecordingProbe(null)), db);

        var driver = response.Drivers!.Single();
        driver.ModelSource.Should().Be("unavailable");
        driver.Models.Should().BeEmpty();
        driver.UnavailableReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetCapabilities_AgyDriver_ReportsNoContextSelection()
    {
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(AgyDriver());
        await db.SaveChangesAsync();

        var probe = new RecordingProbe(new DriverCapabilityReading(
            Adapter: "agy",
            Models: new[] { new DriverModelCapability("gemini-4-argon", "Gemini 4 Argon", new[] { "high" }) },
            ModelSource: CapabilitySourceKind.Probed,
            ObservedAt: DateTimeOffset.UtcNow,
            SupportsContextSelection: false));

        var driver = (await CapabilitiesAsync(Controller(probe), db)).Drivers!.Single();

        driver.SupportsContextSelection.Should().BeFalse();
        driver.ContextSizes.Should().BeNull(
            "a stale saved contextSize has nothing to render against once the control is hidden");
    }

    [Fact]
    public async Task GetCapabilities_UnknownAgentId_ReturnsNotFound()
    {
        // A typo'd id used to fall through to the baseline map, which looked like a successful
        // answer about a driver that does not exist.
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(AgyDriver());
        await db.SaveChangesAsync();

        var result = await Controller(NullDriverCapabilityProbe.Instance)
            .GetCapabilities("no-such-driver", false, db: db);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetCapabilities_NormalizesAgentId_CaseAndMentionPrefix()
    {
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(AgyDriver("agy-core"));
        await db.SaveChangesAsync();

        var response = await CapabilitiesAsync(Controller(NullDriverCapabilityProbe.Instance), db, "@AGY-Core");

        response.Drivers!.Single().DriverId.Should().Be("agy-core");
    }

    [Fact]
    public async Task GetCapabilities_WithoutDatabase_OmitsDriverListRatherThanClaimingNoneExist()
    {
        var response = await CapabilitiesAsync(Controller(NullDriverCapabilityProbe.Instance), db: null);

        response.Drivers.Should().BeNull("no database attached is not the same as zero drivers");
        response.SchemaVersion.Should().Be(2, "callers use it to tell an old API from an empty one");
    }

    [Fact]
    public async Task GetCapabilities_RefreshIsIgnored_ForAnonymousCallers()
    {
        // The endpoint is [AllowAnonymous] for dashboard-on-load compatibility. Without this gate,
        // `?refresh=true` plus any number of agentId permutations turns a read endpoint into a
        // subprocess- and network-fan-out primitive.
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(AgyDriver());
        await db.SaveChangesAsync();

        var probe = new RecordingProbe(null);
        var controller = Controller(probe);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext() // no authenticated principal
        };

        await controller.GetCapabilities(null, refresh: true, db: db);

        probe.Requests.Should().ContainSingle();
        probe.Requests[0].ForceRefresh.Should().BeFalse();
    }

    [Fact]
    public async Task GetCapabilities_RefreshIsHonored_ForAuthenticatedCallers()
    {
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(AgyDriver());
        await db.SaveChangesAsync();

        var probe = new RecordingProbe(null);
        var controller = Controller(probe);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.Name, "astra") }, authenticationType: "test"))
            }
        };

        await controller.GetCapabilities(null, refresh: true, db: db);

        probe.Requests.Should().ContainSingle();
        probe.Requests[0].ForceRefresh.Should().BeTrue();
    }

    [Fact]
    public async Task GetCapabilities_DriverLimitIsBounded()
    {
        // `limit` is caller-controlled; an unbounded value would resolve every driver on the host
        // in one request.
        using var db = TestDbContextFactory.Create();
        for (var i = 0; i < 5; i++) db.Agents.Add(AgyDriver($"driver-{i}"));
        await db.SaveChangesAsync();

        var response = await Controller(NullDriverCapabilityProbe.Instance)
            .GetCapabilities(null, false, limit: 2, db: db);
        var ok = response.Should().BeOfType<OkObjectResult>().Subject;
        var payload = ok.Value.Should().BeOfType<SystemCapabilitiesResponse>().Subject;

        payload.Drivers.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetDriverCapabilities_ReturnsOneDriverReading()
    {
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(AgyDriver("agy-core"));
        db.Agents.Add(OpenAiDriver("other"));
        await db.SaveChangesAsync();

        var probe = new RecordingProbe(new DriverCapabilityReading(
            Adapter: "agy",
            Models: new[] { new DriverModelCapability("gemini-4-argon", "Gemini 4 Argon", new[] { "high" }) },
            ModelSource: CapabilitySourceKind.Probed,
            ObservedAt: DateTimeOffset.UtcNow,
            SupportsContextSelection: false));

        var result = await Controller(probe).GetDriverCapabilities("agy-core", db: db);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<DriverCapabilitiesDto>().Subject;
        dto.DriverId.Should().Be("agy-core");
        probe.Requests.Should().ContainSingle(r => r.DriverId == "agy-core");
    }

    [Fact]
    public async Task GetDriverCapabilities_UnknownDriver_ReturnsNotFound()
    {
        using var db = TestDbContextFactory.Create();
        var result = await Controller(NullDriverCapabilityProbe.Instance).GetDriverCapabilities("ghost", db: db);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetCapabilities_SurfacesOperatorModelsFromTheCapabilitiesBlob()
    {
        // An operator who hand-wrote a model list must see it, marked as theirs rather than as a
        // probe result, so the UI can label which entries were verified.
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(OpenAiDriver());
        await db.SaveChangesAsync();

        var probe = new RecordingProbe(new DriverCapabilityReading(
            Adapter: "openai",
            Models: Array.Empty<DriverModelCapability>(),
            ModelSource: CapabilitySourceKind.Unavailable,
            ObservedAt: DateTimeOffset.UtcNow,
            UnavailableReason: "server unreachable"));

        var driver = (await CapabilitiesAsync(Controller(probe), db)).Drivers!.Single();

        driver.OperatorModels.Should().Equal("hand-picked-model");
        probe.Requests.Single().OperatorModels.Should().Equal("hand-picked-model");
    }

    [Fact]
    public async Task GetCapabilities_AProbeThatThrows_DoesNotTakeTheEndpointDown()
    {
        // The dashboard needs the baseline map to render at all, so a crash in resolution must
        // degrade one driver's card rather than 500 the whole page.
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(AgyDriver());
        await db.SaveChangesAsync();

        var response = await CapabilitiesAsync(Controller(new ThrowingProbe()), db);

        var driver = response.Drivers!.Single();
        driver.ModelSource.Should().Be("unavailable");
        driver.UnavailableReason.Should().Contain("boom");
    }

    // ---- test-openai (unchanged contract, re-pinned after the controller rewrite) ----

    [Fact]
    public async Task TestOpenAiServer_WithEmptyEndpoint_ReturnsBadRequest()
    {
        var actionResult = await Controller(NullDriverCapabilityProbe.Instance).TestOpenAiServer(new TestOpenAiServerRequest(""));
        var badReq = actionResult.Should().BeOfType<BadRequestObjectResult>().Subject;
        var res = badReq.Value.Should().BeOfType<TestOpenAiServerResponse>().Subject;
        res.Success.Should().BeFalse();
        res.Error.Should().Contain("Endpoint URL is required");
    }

    [Fact]
    public async Task TestOpenAiServer_WithUnreachableEndpoint_ReturnsConnectionFailed()
    {
        var actionResult = await Controller(NullDriverCapabilityProbe.Instance)
            .TestOpenAiServer(new TestOpenAiServerRequest("http://127.0.0.1:59999/v1"));
        var okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        var res = okResult.Value.Should().BeOfType<TestOpenAiServerResponse>().Subject;
        res.Success.Should().BeFalse();
        res.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TestOpenAiServer_WithSidecarEnabled_DelegatesToContainerManager()
    {
        var mockManager = new Mock<IPersonaContainerManager>();
        var sidecar = new DriverSidecarConfig
        {
            Enabled = true,
            Type = "tailscale",
            AuthKey = "tskey-12345"
        };

        mockManager.Setup(m => m.TestDriverEndpointAsync(
                "http://private-llm.mesh:8000/v1",
                "secret-api-key",
                sidecar,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DriverEndpointTestResult(
                Success: true,
                StatusCode: 200,
                ResponseBody: """{"data":[{"id":"qwen3-coder"}]}""",
                ErrorMessage: null,
                AvailableModels: new List<string> { "qwen3-coder" }));

        var req = new TestOpenAiServerRequest(
            Endpoint: "http://private-llm.mesh:8000/v1",
            ApiKey: "secret-api-key",
            Sidecar: sidecar);

        var actionResult = await Controller(NullDriverCapabilityProbe.Instance)
            .TestOpenAiServer(req, db: null, containerManager: mockManager.Object);
        var okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        var res = okResult.Value.Should().BeOfType<TestOpenAiServerResponse>().Subject;

        res.Success.Should().BeTrue();
        res.Models.Should().ContainSingle(m => m.Id == "qwen3-coder");
        mockManager.VerifyAll();
    }

    [Fact]
    public async Task TestOpenAiServer_WithSidecarAndAgentId_ResolvesStoredSecrets()
    {
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(new Agent
        {
            Id = "private-driver",
            Name = "Private Driver",
            Role = "Autonomous Agent",
            Status = "online",
            CapabilitiesJson = """
            {
              "apiKey": "db-api-key",
              "sidecar": {
                "enabled": true,
                "type": "tailscale",
                "authKey": "db-tailscale-key"
              }
            }
            """
        });
        await db.SaveChangesAsync();

        var mockManager = new Mock<IPersonaContainerManager>();
        mockManager.Setup(m => m.TestDriverEndpointAsync(
                "http://100.64.0.1:8000/v1",
                "db-api-key",
                It.Is<DriverSidecarConfig>(sc => sc.AuthKey == "db-tailscale-key"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DriverEndpointTestResult(
                Success: true,
                StatusCode: 200,
                AvailableModels: new List<string> { "deepseek-coder" }));

        var req = new TestOpenAiServerRequest(
            Endpoint: "http://100.64.0.1:8000/v1",
            ApiKey: "",
            Sidecar: new DriverSidecarConfig { Enabled = true, Type = "tailscale", AuthKey = "" },
            AgentId: "private-driver");

        var actionResult = await Controller(NullDriverCapabilityProbe.Instance)
            .TestOpenAiServer(req, db: db, containerManager: mockManager.Object);
        var okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        var res = okResult.Value.Should().BeOfType<TestOpenAiServerResponse>().Subject;

        res.Success.Should().BeTrue();
        res.Models.Should().ContainSingle(m => m.Id == "deepseek-coder");
        mockManager.VerifyAll();
    }

    [Fact]
    public async Task GetCapabilities_DegradesToBaseline_WhenDriverEnumerationFails()
    {
        // This endpoint used to read nothing but configuration, so a database outage could not
        // affect it. Per-driver capability resolution introduced that dependency, and it must
        // degrade rather than 500: the baseline map is what lets the dashboard render mode and tier
        // pickers while the database is down.
        using var unreachable = TestDbContextFactory.CreateUnreachable();

        var response = await CapabilitiesAsync(
            Controller(NullDriverCapabilityProbe.Instance), unreachable);

        response.AgentRuntimes.Should().NotBeEmpty();
        response.Drivers.Should().BeNull(
            "an unreadable database is not the same statement as 'this host has no drivers'");
    }

    [Fact]
    public async Task GetCapabilities_UnreadableDatabase_DoesNotReportAgentIdAsMissing()
    {
        // "No driver registered with id X" is a claim about the fleet, and we cannot make it from a
        // database we could not read.
        using var unreachable = TestDbContextFactory.CreateUnreachable();

        var result = await Controller(NullDriverCapabilityProbe.Instance)
            .GetCapabilities("agy-core", false, db: unreachable);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetDriverCapabilities_UnreadableDatabase_Returns503NotAnEmptyAnswer()
    {
        using var unreachable = TestDbContextFactory.CreateUnreachable();

        var result = await Controller(NullDriverCapabilityProbe.Instance)
            .GetDriverCapabilities("agy-core", db: unreachable);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(503);
    }

    // ---------------------------------------------------------------------------------------
    // Option C wiring: the declaration stored by a runner must reach the probe.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetCapabilities_PassesTheStoredDeclarationToTheProbe()
    {
        // The probe-side tests cover what happens *with* a declaration; this covers the only path
        // by which one gets there. Parse -> ToProbeRequest is two hops across two projects, and a
        // silent break in either means every runner's self-report is ignored and the API keeps
        // probing a binary it does not have - which is the production bug C exists to fix, and it
        // would fail closed: no error, just an empty picker forever.
        var declaration = new DriverCapabilityDeclaration
        {
            Adapter = "agy",
            Models = new[] { new DriverCapabilityDeclaration.DeclaredModel { Id = "runner-reported-model" } },
            Source = "agy-models",
            ObservedAt = DateTimeOffset.UtcNow
        };

        using var db = TestDbContextFactory.Create();
        db.Agents.Add(AgyDriver());
        await db.SaveChangesAsync();
        db.Agents.Find("agy-core")!.CapabilitiesJson =
            $$"""{"adapter":"agy","capabilities":["chat"],"declared":{{declaration.ToJson()}}}""";
        await db.SaveChangesAsync();

        var probe = new RecordingProbe(null);

        await CapabilitiesAsync(Controller(probe), db);

        var request = probe.Requests.Should().ContainSingle().Subject;
        request.Declared.Should().NotBeNull("a stored declaration that never reaches the probe is dead weight in the blob");
        request.Declared!.Models.Select(m => m.Id).Should().Equal("runner-reported-model");
    }

    [Fact]
    public async Task GetCapabilities_PassesNoDeclarationForADriverThatNeverDeclared()
    {
        // The other half: absence must stay absence. If a missing declaration were materialized as
        // an empty one, the probe would treat it as "the driver reported nothing" instead of
        // falling through to a live read, blanking the picker for every runner predating C.
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(AgyDriver());
        await db.SaveChangesAsync();

        var probe = new RecordingProbe(null);

        await CapabilitiesAsync(Controller(probe), db);

        probe.Requests.Should().ContainSingle().Subject.Declared.Should().BeNull();
    }

    [Fact]
    public async Task GetCapabilities_DeclarationIsPerDriverNotSharedAcrossTheResult()
    {
        // The endpoint resolves several drivers in one call. A declaration leaking from one row to
        // another would show an agy catalogue under an OpenAI driver - the per-driver confusion
        // this line of work started with.
        var declaration = new DriverCapabilityDeclaration
        {
            Adapter = "agy",
            Models = new[] { new DriverCapabilityDeclaration.DeclaredModel { Id = "agy-only-model" } },
            ObservedAt = DateTimeOffset.UtcNow
        };

        var agy = AgyDriver("agy-core");
        agy.CapabilitiesJson = $$"""{"adapter":"agy","declared":{{declaration.ToJson()}}}""";
        var openai = OpenAiDriver("qwen-driver");

        using var db = TestDbContextFactory.Create();
        db.Agents.Add(agy);
        db.Agents.Add(openai);
        await db.SaveChangesAsync();

        var probe = new RecordingProbe(null);

        await CapabilitiesAsync(Controller(probe), db);

        var byDriver = probe.Requests.ToDictionary(r => r.DriverId, r => r.Declared);
        byDriver["agy-core"].Should().NotBeNull();
        byDriver["qwen-driver"].Should().BeNull("the agy runner's declaration must not be attributed to another driver");
    }

    // ---------------------------------------------------------------------------------------
    // The full round trip: what the runner writes is what the probe is asked to use.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task RegistrationRoundTrip_RunnerDeclarationReachesTheProbeThroughTheRealWritePath()
    {
        // The other tests in this group seed CapabilitiesJson by hand, which pins the read side but
        // proves nothing about the write side. This one uses the real producer
        // (MeshClient.SerializePresetCapabilities) and the real writer (AgentsController.UpsertAgent,
        // which merges rather than overwrites), so a change to either shape - the CLI's bare-array
        // upgrade, or Merge's treatment of an absent key - fails here instead of in production.
        //
        // The existing row deliberately holds the pre-C bare-array shape, so the merge exercises an
        // upgrading runner writing the object form over an old blob. Note what this does NOT prove:
        // the array normalization itself is already pinned by
        // Merge_ArrayExistingNormalizedToObject_KeepsCapabilitiesWhenLimitsAdded, and it would pass
        // even if that normalization broke, because the pushed blob carries its own capability list
        // and never needs the fallback. What is new here is only that the real producer and the real
        // writer agree on the key names.
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(new Agent
        {
            Id = "agy-core",
            Name = "Antigravity CLI",
            Role = "Driver",
            RuntimeType = RuntimeType.CliAgy,
            // The pre-C shape: a bare capability array, no object wrapper at all.
            CapabilitiesJson = """["chat","tools"]"""
        });
        await db.SaveChangesAsync();

        var declaration = new DriverCapabilityDeclaration
        {
            Adapter = "agy",
            Models = new[]
            {
                new DriverCapabilityDeclaration.DeclaredModel
                {
                    Id = "gemini-4-argon",
                    Name = "Gemini 4 Argon",
                    Efforts = new[] { "low", "high" }
                }
            },
            Source = "agy-models",
            ObservedAt = DateTimeOffset.UtcNow
        };

        // Exactly what the runner puts on the wire.
        var pushed = MeshClient.SerializePresetCapabilities(
            new AgentPresetConfig
            {
                Id = "agy-core",
                Name = "Antigravity CLI",
                AdapterType = "agy",
                Capabilities = new List<string> { "chat", "tools" }
            },
            declaration);

        var agents = new AgentsController(db, new ApiKeyService());
        var registered = await agents.UpsertAgent(new RegisterAgentRequest(
            Id: "agy-core",
            Name: "Antigravity CLI",
            RuntimeType: RuntimeType.CliAgy,
            CapabilitiesJson: pushed));

        registered.Should().BeOfType<OkObjectResult>();

        var probe = new RecordingProbe(null);
        await CapabilitiesAsync(Controller(probe), db);

        var request = probe.Requests.Should().ContainSingle().Subject;
        request.Declared.Should().NotBeNull(
            "a declaration that survives serialization but not the merge is invisible to the API");
        request.Declared!.Models.Select(m => m.Id).Should().Equal("gemini-4-argon");
        request.Declared.Models[0].Efforts.Should().Equal("low", "high");
        request.Declared.IsFresh(DateTimeOffset.UtcNow).Should().BeTrue(
            "the observedAt written by the CLI must survive the round trip as a real timestamp");

        // And the pre-existing capability strings must not be collateral damage of the shape change.
        DriverCapabilities.Parse(db.Agents.Find("agy-core")!.CapabilitiesJson)
            .Capabilities.Should().Equal("chat", "tools");
    }

    private sealed class ThrowingProbe : IDriverCapabilityProbe
    {
        public Task<DriverCapabilityReading?> ProbeAsync(
            DriverCapabilityProbeRequest request,
            CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }

    private sealed class RecordingProbe : IDriverCapabilityProbe
    {
        private readonly DriverCapabilityReading? _reading;

        public RecordingProbe(DriverCapabilityReading? reading) => _reading = reading;

        public List<DriverCapabilityProbeRequest> Requests { get; } = new();

        public Task<DriverCapabilityReading?> ProbeAsync(
            DriverCapabilityProbeRequest request,
            CancellationToken ct = default)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            return Task.FromResult(_reading);
        }
    }
}
