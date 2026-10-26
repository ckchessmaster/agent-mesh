using AgentMesh.Api.Controllers;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AgentMesh.Tests.Controllers;

public class SystemControllerTests
{
    private readonly SystemController _controller = new();

    [Fact]
    public async Task GetCapabilities_ReturnsAgentRuntimes()
    {
        var actionResult = await _controller.GetCapabilities();
        var okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<SystemCapabilitiesResponse>().Subject;

        response.AgentRuntimes.Should().NotBeNull();
        response.AgentRuntimes.Should().ContainKey("agy");
        response.AgentRuntimes.Should().ContainKey("copilot");
        response.AgentRuntimes.Should().ContainKey("claude");
        response.AgentRuntimes.Should().ContainKey("openai");
        response.AgentRuntimes.Should().ContainKey("generic");

        var openai = response.AgentRuntimes["openai"];
        openai.Adapter.Should().Be("openai");
        openai.Name.Should().Contain("OpenAI");
        openai.Modes.Should().Contain(m => m.Id == "auto");
        openai.Modes.Should().Contain(m => m.Id == "plan");
        openai.Modes.Should().Contain(m => m.Id == "direct");
        openai.Models.Should().Contain(m => m.Id == "llama3.3");
        openai.Models.Should().Contain(m => m.Id == "qwen2.5-coder");
        openai.Models.Should().Contain(m => m.Id == "deepseek-r1");
        openai.ContextSizes.Should().NotBeNull();
        openai.ContextSizes!.Should().Contain(c => c.Id == "128k");

        var agy = response.AgentRuntimes["agy"];
        agy.Adapter.Should().Be("agy");
        agy.Modes.Should().Contain(m => m.Id == "auto");
        agy.Modes.Should().Contain(m => m.Id == "plan");
        agy.Modes.Should().Contain(m => m.Id == "accept-edits");

        var copilot = response.AgentRuntimes["copilot"];
        copilot.Adapter.Should().Be("copilot");
        copilot.Modes.Should().Contain(m => m.Id == "autopilot");
        copilot.Models.Should().Contain(m => m.Id == "gpt-6.1-sol");
        copilot.Models.Should().Contain(m => m.Id == "claude-sonnet-5.5");
        copilot.Tiers.Should().NotBeNull();
        copilot.Tiers!.Should().Contain(t => t.Id == "intelligence");
    }

    [Fact]
    public async Task TestOpenAiServer_WithEmptyEndpoint_ReturnsBadRequest()
    {
        var actionResult = await _controller.TestOpenAiServer(new TestOpenAiServerRequest(""));
        var badReq = actionResult.Should().BeOfType<BadRequestObjectResult>().Subject;
        var res = badReq.Value.Should().BeOfType<TestOpenAiServerResponse>().Subject;
        res.Success.Should().BeFalse();
        res.Error.Should().Contain("Endpoint URL is required");
    }

    [Fact]
    public async Task TestOpenAiServer_WithUnreachableEndpoint_ReturnsConnectionFailed()
    {
        var actionResult = await _controller.TestOpenAiServer(new TestOpenAiServerRequest("http://127.0.0.1:59999/v1"));
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

        var actionResult = await _controller.TestOpenAiServer(req, db: null, containerManager: mockManager.Object);
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

        // Request has empty ApiKey and empty AuthKey, but passes AgentId
        var req = new TestOpenAiServerRequest(
            Endpoint: "http://100.64.0.1:8000/v1",
            ApiKey: "",
            Sidecar: new DriverSidecarConfig { Enabled = true, Type = "tailscale", AuthKey = "" },
            AgentId: "private-driver");

        var actionResult = await _controller.TestOpenAiServer(req, db: db, containerManager: mockManager.Object);
        var okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        var res = okResult.Value.Should().BeOfType<TestOpenAiServerResponse>().Subject;

        res.Success.Should().BeTrue();
        res.Models.Should().ContainSingle(m => m.Id == "deepseek-coder");
        mockManager.VerifyAll();
    }
}
