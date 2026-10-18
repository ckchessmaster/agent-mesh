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
        response.AgentRuntimes.Should().ContainKey("generic");

        var agy = response.AgentRuntimes["agy"];
        agy.Adapter.Should().Be("agy");
        agy.Modes.Should().Contain(m => m.Id == "auto");
        agy.Modes.Should().Contain(m => m.Id == "plan");
        agy.Modes.Should().Contain(m => m.Id == "accept-edits");

        var copilot = response.AgentRuntimes["copilot"];
        copilot.Adapter.Should().Be("copilot");
        copilot.Modes.Should().Contain(m => m.Id == "autopilot");
        copilot.Models.Should().Contain(m => m.Id == "claude-3.7-sonnet");
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
}
