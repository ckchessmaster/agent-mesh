using AgentMesh.Api.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;

namespace AgentMesh.Tests.Controllers;

public class SystemControllerTests
{
    private readonly SystemController _controller = new();

    [Fact]
    public async Task GetCapabilities_ReturnsWorkspaceDefaults_AndAgentRuntimes()
    {
        var actionResult = await _controller.GetCapabilities();
        var okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<SystemCapabilitiesResponse>().Subject;

        response.Workspace.Should().NotBeNull();
        response.Workspace.DefaultSandboxPath.Should().Be("~/.agentmesh/workspaces/<session-id>");
        response.Workspace.CurrentDirectory.Should().NotBeNullOrWhiteSpace();
        response.Workspace.Bookmarks.Should().NotBeEmpty();

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
    public void BrowseFilesystem_WithCurrentDirectory_ReturnsSubdirectories()
    {
        var currentDir = Directory.GetCurrentDirectory();
        var actionResult = _controller.BrowseFilesystem(currentDir);

        var okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<FilesystemBrowseResponse>().Subject;

        response.CurrentPath.Should().Be(currentDir);
        response.Directories.Should().NotBeNull();
        // Should not contain hidden or bin/obj noise
        response.Directories.Should().NotContain(d => d.Name.StartsWith("."));
        response.Directories.Should().NotContain(d => d.Name.Equals("bin", StringComparison.OrdinalIgnoreCase));
        response.Directories.Should().NotContain(d => d.Name.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BrowseFilesystem_WithInvalidDirectory_FallsBackToCurrentOrHome()
    {
        var actionResult = _controller.BrowseFilesystem("/path/that/does/not/exist/9999");

        var okResult = actionResult.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<FilesystemBrowseResponse>().Subject;

        response.CurrentPath.Should().NotBeNullOrWhiteSpace();
        Directory.Exists(response.CurrentPath).Should().BeTrue();
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
}
