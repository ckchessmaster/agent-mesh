using AgentMesh.Api.Controllers;
using AgentMesh.Api.Services;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Xunit;

namespace AgentMesh.Tests.Controllers;

/// <summary>
/// Regression cover for the round-trip that used to eat driver limits: the dashboard rebuilt
/// capabilitiesJson from the four fields it rendered, and the API replaced the blob wholesale.
/// Result: maxSteps / timeoutSeconds / maxRetries vanished on every save, and the CLI pushed the
/// loss back into its own local config on next connect.
/// </summary>
public class AgentsControllerConfigTests
{
    private readonly IApiKeyService _apiKeyService = new ApiKeyService();

    private const string FullOpenAiCaps = """
        {"adapter":"openai","endpoint":"http://127.0.0.1:11435/v1","apiKey":"super-secret",
         "defaultModel":"qwen3","timeoutSeconds":300,"maxRetries":4,"retryDelayMilliseconds":250,
         "maxSteps":100,"capabilities":["run_command","read_file","ask_question"]}
        """;

    private (AgentsController Controller, AgentMeshDbContext Db) Arrange(string capabilitiesJson)
    {
        var db = TestDbContextFactory.Create();
        db.Agents.Add(new Agent
        {
            Id = "spence-ai",
            Name = "Spence Private AI",
            Role = "Autonomous Agent",
            Status = "online",
            RuntimeType = RuntimeType.OpenAiCompatible,
            TrustTier = TrustTier.SeniorManager,
            CapabilitiesJson = capabilitiesJson
        });
        db.SaveChanges();
        return (new AgentsController(db, _apiKeyService), db);
    }

    [Fact]
    public async Task UpsertAgent_DashboardStylePatch_PreservesLimitsTheFormDoesNotRender()
    {
        var (controller, db) = Arrange(FullOpenAiCaps);

        // Exactly the payload the dashboard's register form used to send.
        var dashboardPayload = """
            {"adapter":"openai","endpoint":"http://127.0.0.1:11435/v1","apiKey":"super-secret",
             "defaultModel":"qwen3","models":[{"id":"qwen3","name":"qwen3"}]}
            """;

        var result = await controller.UpsertAgent(new RegisterAgentRequest(
            Id: "spence-ai", Name: "Spence Private AI", CapabilitiesJson: dashboardPayload));

        result.Should().BeOfType<OkObjectResult>();
        var stored = db.Agents.Single(a => a.Id == "spence-ai").CapabilitiesJson;
        using var doc = JsonDocument.Parse(stored);

        doc.RootElement.GetProperty("maxSteps").GetInt32().Should().Be(100);
        doc.RootElement.GetProperty("timeoutSeconds").GetInt32().Should().Be(300);
        doc.RootElement.GetProperty("maxRetries").GetInt32().Should().Be(4);
        doc.RootElement.GetProperty("retryDelayMilliseconds").GetInt32().Should().Be(250);
        doc.RootElement.GetProperty("capabilities").GetArrayLength().Should().Be(3);
        // And the patch's own change still lands.
        doc.RootElement.GetProperty("models").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task UpsertAgent_CliStylePushWithNullOptionals_DoesNotEraseServerValues()
    {
        var (controller, db) = Arrange(FullOpenAiCaps);

        // The CLI emits null for every optional it doesn't hold locally.
        var cliPayload = """
            {"adapter":"openai","endpoint":"http://127.0.0.1:11435/v1","apiKey":"super-secret",
             "defaultModel":null,"timeoutSeconds":null,"maxRetries":null,
             "retryDelayMilliseconds":null,"maxSteps":null,"capabilities":["run_command"]}
            """;

        var result = await controller.UpsertAgent(new RegisterAgentRequest(
            Id: "spence-ai", Name: "Spence Private AI", CapabilitiesJson: cliPayload));

        result.Should().BeOfType<OkObjectResult>();
        using var doc = JsonDocument.Parse(db.Agents.Single(a => a.Id == "spence-ai").CapabilitiesJson);

        doc.RootElement.GetProperty("maxSteps").GetInt32().Should().Be(100);
        doc.RootElement.GetProperty("timeoutSeconds").GetInt32().Should().Be(300);
    }

    [Fact]
    public async Task UpsertAgent_CliPushesRealValue_OverridesServerValue()
    {
        // Null means "unset", a real number means "this is my config" — last writer wins.
        var (controller, db) = Arrange(FullOpenAiCaps);

        var cliPayload = """{"adapter":"openai","maxSteps":25}""";
        await controller.UpsertAgent(new RegisterAgentRequest(
            Id: "spence-ai", Name: "Spence Private AI", CapabilitiesJson: cliPayload));

        using var doc = JsonDocument.Parse(db.Agents.Single(a => a.Id == "spence-ai").CapabilitiesJson);
        doc.RootElement.GetProperty("maxSteps").GetInt32().Should().Be(25);
    }

    [Fact]
    public async Task UpdateDriverConfig_PartialPatch_KeepsUntouchedFieldsAndApiKey()
    {
        var (controller, db) = Arrange(FullOpenAiCaps);

        var result = await controller.UpdateDriverConfig("spence-ai", new UpdateDriverConfigRequest(
            MaxSteps: 500,
            TimeoutSeconds: 900));

        var view = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should()
            .BeOfType<DriverCapabilitiesView>().Subject;

        view.MaxSteps.Should().Be(500);
        view.TimeoutSeconds.Should().Be(900);
        view.MaxRetries.Should().Be(4);
        view.Endpoint.Should().Be("http://127.0.0.1:11435/v1");
        view.DefaultModel.Should().Be("qwen3");
        view.HasApiKey.Should().BeTrue();

        // The secret itself must still be intact in the blob even though the patch never carried it.
        var stored = db.Agents.Single(a => a.Id == "spence-ai").CapabilitiesJson;
        stored.Should().Contain("super-secret");
    }

    [Fact]
    public async Task UpdateDriverConfig_BlankApiKey_KeepsStoredKey()
    {
        var (controller, db) = Arrange(FullOpenAiCaps);

        await controller.UpdateDriverConfig("spence-ai", new UpdateDriverConfigRequest(
            ApiKey: "   ", MaxSteps: 12));

        var stored = db.Agents.Single(a => a.Id == "spence-ai").CapabilitiesJson;
        stored.Should().Contain("super-secret");
        using var doc = JsonDocument.Parse(stored);
        doc.RootElement.GetProperty("apiKey").GetString().Should().Be("super-secret");
    }

    [Fact]
    public async Task UpdateDriverConfig_NewApiKey_ReplacesStoredKey()
    {
        var (controller, db) = Arrange(FullOpenAiCaps);

        await controller.UpdateDriverConfig("spence-ai", new UpdateDriverConfigRequest(ApiKey: "rotated-key"));

        using var doc = JsonDocument.Parse(db.Agents.Single(a => a.Id == "spence-ai").CapabilitiesJson);
        doc.RootElement.GetProperty("apiKey").GetString().Should().Be("rotated-key");
    }

    [Fact]
    public async Task UpdateDriverConfig_EmptyRequest_ChangesNothing()
    {
        var (controller, db) = Arrange(FullOpenAiCaps);

        var result = await controller.UpdateDriverConfig("spence-ai", new UpdateDriverConfigRequest());

        result.Should().BeOfType<OkObjectResult>();
        db.Agents.Single(a => a.Id == "spence-ai").CapabilitiesJson.Should().Be(FullOpenAiCaps);
    }

    [Fact]
    public async Task UpdateDriverConfig_OnAgentWithBareArrayCaps_CreatesObjectEnvelope()
    {
        var (controller, db) = Arrange("""["bash","grep"]""");

        await controller.UpdateDriverConfig("spence-ai", new UpdateDriverConfigRequest(MaxSteps: 33));

        var stored = db.Agents.Single(a => a.Id == "spence-ai").CapabilitiesJson;
        using var doc = JsonDocument.Parse(stored);
        doc.RootElement.GetProperty("maxSteps").GetInt32().Should().Be(33);
        doc.RootElement.GetProperty("capabilities").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task UpdateDriverConfig_UnknownDriver_ReturnsNotFound()
    {
        var (controller, _) = Arrange(FullOpenAiCaps);

        var result = await controller.UpdateDriverConfig("ghost", new UpdateDriverConfigRequest(MaxSteps: 1));

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetDriverConfig_ReturnsParsedView_WithoutLeakingApiKey()
    {
        var (controller, _) = Arrange(FullOpenAiCaps);

        var result = await controller.GetDriverConfig("spence-ai");

        var view = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should()
            .BeOfType<DriverCapabilitiesView>().Subject;

        view.MaxSteps.Should().Be(100);
        view.TimeoutSeconds.Should().Be(300);
        view.MaxRetries.Should().Be(4);
        view.RetryDelayMilliseconds.Should().Be(250);
        view.HasApiKey.Should().BeTrue();
        view.Adapter.Should().Be("openai");

        // The view type has no ApiKey member at all — assert it stays that way, since a future
        // "just add the field" change would put a secret in the browser bundle.
        typeof(DriverCapabilitiesView).GetProperties()
            .Select(p => p.Name)
            .Should().NotContain(new[] { "ApiKey", "apikey", "apiKey" });
    }

    [Fact]
    public async Task GetDriverConfig_OnLegacyArrayCaps_ReturnsCapabilitiesOnly()
    {
        var (controller, _) = Arrange("""["bash","read_file","grep"]""");

        var view = (await controller.GetDriverConfig("spence-ai")).Should().BeOfType<OkObjectResult>()
            .Subject.Value.Should().BeOfType<DriverCapabilitiesView>().Subject;

        view.Capabilities.Should().Equal("bash", "read_file", "grep");
        view.MaxSteps.Should().BeNull();
        view.Adapter.Should().BeNull();
    }

    [Fact]
    public async Task GetDriverConfig_UnknownDriver_ReturnsNotFound()
    {
        var (controller, _) = Arrange(FullOpenAiCaps);

        (await controller.GetDriverConfig("ghost")).Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetAgents_ExposesParsedConfigOnEachDriver()
    {
        var (controller, _) = Arrange(FullOpenAiCaps);

        var result = await controller.GetAgents();

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var agents = okResult.Value.Should().BeAssignableTo<IEnumerable<AgentDto>>().Subject.ToList();

        var dto = agents.Single(a => a.Id == "spence-ai");
        dto.Config.Should().NotBeNull();
        dto.Config!.MaxSteps.Should().Be(100);
        dto.Config.Endpoint.Should().Be("http://127.0.0.1:11435/v1");
    }

    [Fact]
    public async Task GetAgents_ParsedConfigSurface_NeverCarriesApiKey()
    {
        // The new `config` view is what the dashboard renders, so it must stay key-free.
        // NOTE: the sibling `capabilitiesJson` field still ships the raw blob (and with it the
        // plaintext key) over an [AllowAnonymous] endpoint. That leak predates this change and is
        // not fixed here: the CLI reads apiKey back out of that blob in PopulateFromRemote, so
        // redacting it is a separate decision about where driver secrets live.
        var (controller, _) = Arrange(FullOpenAiCaps);

        var result = await controller.GetAgents();
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var agents = okResult.Value.Should().BeAssignableTo<IEnumerable<AgentDto>>().Subject.ToList();

        var config = agents.Single(a => a.Id == "spence-ai").Config;
        config.Should().NotBeNull();
        config!.MaxSteps.Should().Be(100);
        config.HasApiKey.Should().BeTrue(); // presence is reportable, the value is not

        var configJson = JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        configJson.Should().NotContain("super-secret");
    }

    [Fact]
    public async Task GetAgents_ConfigSerializesAsCamelCase_MatchingDashboardTypes()
    {
        // The dashboard's DriverConfig TS interface is hand-written against these exact names.
        // If the naming policy ever changes, this fails before the UI silently reads undefined.
        var (controller, _) = Arrange(FullOpenAiCaps);

        var result = await controller.GetAgents();
        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;

        var json = JsonSerializer.Serialize(okResult.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.Should().Contain("\"maxSteps\":100");
        json.Should().Contain("\"timeoutSeconds\":300");
        json.Should().Contain("\"retryDelayMilliseconds\":250");
        json.Should().Contain("\"hasApiKey\":true");
        json.Should().Contain("\"defaultModel\":\"qwen3\"");
    }

    [Fact]
    public async Task UpsertAgent_BringsUpNewDriver_StoresBlobAsGiven()
    {
        // Nothing to merge with on a first write.
        using var db = TestDbContextFactory.Create();
        var controller = new AgentsController(db, _apiKeyService);

        var result = await controller.UpsertAgent(new RegisterAgentRequest(
            Id: "Brand-New", Name: "Brand New", CapabilitiesJson: """{"adapter":"openai","maxSteps":7}"""));

        result.Should().BeOfType<CreatedAtActionResult>();
        var stored = db.Agents.Single(a => a.Id == "brand-new").CapabilitiesJson;
        using var doc = JsonDocument.Parse(stored);
        doc.RootElement.GetProperty("maxSteps").GetInt32().Should().Be(7);
    }

    [Fact]
    public async Task UpdateDriverConfig_WithSidecar_PersistsAndReturnsMaskedSidecarConfig()
    {
        var (controller, db) = Arrange(FullOpenAiCaps);

        var sidecarReq = new DriverSidecarConfig
        {
            Enabled = true,
            Type = "tailscale",
            AuthKey = "tskey-auth-secret-test",
            Hostname = "mesh-private-llm",
            UserspaceMode = false,
            ExtraArgs = "--accept-routes"
        };

        var result = await controller.UpdateDriverConfig("spence-ai", new UpdateDriverConfigRequest(
            Sidecar: sidecarReq
        ));

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var view = okResult.Value.Should().BeOfType<DriverCapabilitiesView>().Subject;

        view.Sidecar.Should().NotBeNull();
        view.Sidecar!.Enabled.Should().BeTrue();
        view.Sidecar.Type.Should().Be("tailscale");
        view.Sidecar.Hostname.Should().Be("mesh-private-llm");
        view.Sidecar.AuthKey.Should().BeNull("secret authKey must be masked on returned view");
        view.HasSidecarAuthKey.Should().BeTrue();

        var stored = db.Agents.Single(a => a.Id == "spence-ai").CapabilitiesJson;
        using var doc = JsonDocument.Parse(stored);
        var sc = doc.RootElement.GetProperty("sidecar");
        sc.GetProperty("authKey").GetString().Should().Be("tskey-auth-secret-test");
        sc.GetProperty("hostname").GetString().Should().Be("mesh-private-llm");
    }
}
