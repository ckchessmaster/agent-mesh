using System.Security.Cryptography;
using System.Text;
using AgentMesh.Api.Controllers;
using AgentMesh.Api.Hubs;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Services;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Controllers;

public class McpServersControllerTests
{
    private readonly ICredentialEncryptionService _encryption = new AesCredentialEncryptionService(
        SHA256.HashData(Encoding.UTF8.GetBytes("mcp-servers-controller-test-key-32")));

    private readonly Mock<IHubContext<AgentMeshHub>> _mockHubContext;
    private readonly Mock<IClientProxy> _mockClientProxy;

    public McpServersControllerTests()
    {
        _mockHubContext = new Mock<IHubContext<AgentMeshHub>>();
        var mockClients = new Mock<IHubClients>();
        _mockClientProxy = new Mock<IClientProxy>();
        mockClients.Setup(c => c.All).Returns(_mockClientProxy.Object);
        _mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);
    }

    [Fact]
    public async Task CreateMcpServer_StdioWithEnv_EncryptsSecretsAndMasksDto()
    {
        await using var db = TestDbContextFactory.Create();
        var controller = new McpServersController(db, _encryption, _mockHubContext.Object);

        var res = await controller.CreateMcpServer(new CreateMcpServerRequest(
            Name: "GitHub Official",
            Description: "GitHub MCP server",
            Transport: McpTransportType.Stdio,
            Command: "npx",
            Args: new List<string> { "-y", "@modelcontextprotocol/server-github" },
            Env: new Dictionary<string, string>
            {
                ["GITHUB_PERSONAL_ACCESS_TOKEN"] = "ghp_SecretCatalogToken9876",
                ["LOG_LEVEL"] = "info"
            },
            TimeoutSeconds: 45
        ));

        var created = res.Should().BeOfType<CreatedAtActionResult>().Subject;
        var dto = created.Value.Should().BeOfType<McpServerDto>().Subject;

        dto.Id.Should().Be("github-official");
        dto.Name.Should().Be("GitHub Official");
        dto.Transport.Should().Be(McpTransportType.Stdio);
        dto.Command.Should().Be("npx");
        dto.Args.Should().Equal("-y", "@modelcontextprotocol/server-github");
        dto.Url.Should().BeNull();
        dto.TimeoutSeconds.Should().Be(45);
        dto.Enabled.Should().BeTrue();
        dto.HasEnv.Should().BeTrue();
        dto.HasHeaders.Should().BeFalse();
        dto.EnvMasked.Should().ContainKey("GITHUB_PERSONAL_ACCESS_TOKEN")
            .WhoseValue.Should().Be("ghp_...9876");
        dto.EnvMasked["GITHUB_PERSONAL_ACCESS_TOKEN"].Should().NotContain("SecretCatalogToken");

        var entity = await db.McpServers.FindAsync("github-official");
        entity.Should().NotBeNull();
        entity!.EnvEncrypted.Should().NotBeNullOrWhiteSpace();
        entity.EnvEncrypted.Should().NotContain("ghp_SecretCatalogToken9876");

        var decrypted = McpSecretProtector.DecryptDictionary(_encryption, entity.EnvEncrypted);
        decrypted["GITHUB_PERSONAL_ACCESS_TOKEN"].Should().Be("ghp_SecretCatalogToken9876");
        decrypted["LOG_LEVEL"].Should().Be("info");

        _mockClientProxy.Verify(
            c => c.SendCoreAsync("McpServerCreated", It.Is<object?[]>(args => args.Length == 1), default),
            Times.Once);
    }

    [Fact]
    public async Task CreateMcpServer_HttpWithHeaders_EncryptsHeadersAndClearsCommand()
    {
        await using var db = TestDbContextFactory.Create();
        var controller = new McpServersController(db, _encryption);

        var res = await controller.CreateMcpServer(new CreateMcpServerRequest(
            Name: "Context7 Remote",
            Id: "context7",
            Transport: McpTransportType.Http,
            Command: "should-be-ignored",
            Url: "https://mcp.context7.com/mcp",
            Headers: new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer ctx7_live_secret_1234"
            }
        ));

        var created = res.Should().BeOfType<CreatedAtActionResult>().Subject;
        var dto = created.Value.Should().BeOfType<McpServerDto>().Subject;

        dto.Id.Should().Be("context7");
        dto.Transport.Should().Be(McpTransportType.Http);
        dto.Command.Should().BeNull();
        dto.Args.Should().BeEmpty();
        dto.Url.Should().Be("https://mcp.context7.com/mcp");
        dto.HasHeaders.Should().BeTrue();
        dto.HeadersMasked.Should().ContainKey("Authorization")
            .WhoseValue.Should().Be("Bear...1234");
    }

    [Theory]
    [InlineData("", McpTransportType.Stdio, "npx", null)]
    [InlineData("Missing Command", McpTransportType.Stdio, null, null)]
    [InlineData("Missing Url", McpTransportType.Http, null, null)]
    [InlineData("Invalid Url Scheme", McpTransportType.Sse, null, "ftp://example.com/sse")]
    [InlineData("Invalid Timeout", McpTransportType.Stdio, "npx", null, 0)]
    public async Task CreateMcpServer_InvalidRequests_ReturnsBadRequest(
        string name,
        McpTransportType transport,
        string? command,
        string? url,
        int timeoutSeconds = 30)
    {
        await using var db = TestDbContextFactory.Create();
        var controller = new McpServersController(db, _encryption);

        var res = await controller.CreateMcpServer(new CreateMcpServerRequest(
            Name: name,
            Transport: transport,
            Command: command,
            Url: url,
            TimeoutSeconds: timeoutSeconds
        ));

        res.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CreateMcpServer_DuplicateSlug_ReturnsConflict()
    {
        await using var db = TestDbContextFactory.Create();
        var controller = new McpServersController(db, _encryption);

        await controller.CreateMcpServer(new CreateMcpServerRequest(
            Name: "GitHub",
            Id: "github",
            Transport: McpTransportType.Stdio,
            Command: "npx"
        ));

        var duplicate = await controller.CreateMcpServer(new CreateMcpServerRequest(
            Name: "GitHub Duplicate",
            Id: "github",
            Transport: McpTransportType.Stdio,
            Command: "uvx"
        ));

        duplicate.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task GetMcpServers_FiltersByEnabledAndTransport_AndCountsAssignments()
    {
        await using var db = TestDbContextFactory.Create();
        var controller = new McpServersController(db, _encryption);

        await controller.CreateMcpServer(new CreateMcpServerRequest(
            Name: "Alpha Stdio",
            Id: "alpha",
            Transport: McpTransportType.Stdio,
            Command: "npx",
            Enabled: true
        ));
        await controller.CreateMcpServer(new CreateMcpServerRequest(
            Name: "Beta Http",
            Id: "beta",
            Transport: McpTransportType.Http,
            Url: "https://beta.example.com/mcp",
            Enabled: false
        ));
        await controller.CreateMcpServer(new CreateMcpServerRequest(
            Name: "Gamma Http",
            Id: "gamma",
            Transport: McpTransportType.Http,
            Url: "https://gamma.example.com/mcp",
            Enabled: true
        ));

        db.Personas.Add(new Persona { Id = "astra", Name = "Astra", DriverId = "agy" });
        db.PersonaMcpServers.Add(new PersonaMcpServer { PersonaId = "astra", McpServerId = "gamma" });
        await db.SaveChangesAsync();

        var enabledHttpRes = await controller.GetMcpServers(enabled: true, transport: McpTransportType.Http);
        var list = enabledHttpRes.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<IEnumerable<McpServerDto>>().Subject.ToList();

        list.Should().HaveCount(1);
        list[0].Id.Should().Be("gamma");
        list[0].AssignedPersonaCount.Should().Be(1);

        var missingRes = await controller.GetMcpServer("does-not-exist");
        missingRes.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task UpdateMcpServer_PreservesSecretsWhenOmitted_AndSupportsMergeAndRemove()
    {
        await using var db = TestDbContextFactory.Create();
        var controller = new McpServersController(db, _encryption, _mockHubContext.Object);

        await controller.CreateMcpServer(new CreateMcpServerRequest(
            Name: "Postgres",
            Id: "postgres",
            Transport: McpTransportType.Stdio,
            Command: "npx",
            Args: new List<string> { "-y", "@modelcontextprotocol/server-postgres" },
            Env: new Dictionary<string, string>
            {
                ["PGPASSWORD"] = "super_secret_pw_1234",
                ["PGUSER"] = "readonly_user"
            }
        ));

        // 1. Update metadata only (Env == null) -> existing secrets must remain intact
        var metaUpdateRes = await controller.UpdateMcpServer("postgres", new UpdateMcpServerRequest(
            Name: "Postgres ReadOnly",
            Enabled: false,
            TimeoutSeconds: 60
        ));

        var metaDto = metaUpdateRes.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<McpServerDto>().Subject;
        metaDto.Name.Should().Be("Postgres ReadOnly");
        metaDto.Enabled.Should().BeFalse();
        metaDto.TimeoutSeconds.Should().Be(60);
        metaDto.HasEnv.Should().BeTrue();
        metaDto.EnvMasked.Should().ContainKeys("PGPASSWORD", "PGUSER");

        // 2. Merge new env key and remove PGUSER without touching PGPASSWORD
        var mergeRes = await controller.UpdateMcpServer("postgres", new UpdateMcpServerRequest(
            Env: new Dictionary<string, string> { ["PGHOST"] = "db.internal" },
            MergeEnv: true,
            RemoveEnvKeys: new List<string> { "PGUSER" }
        ));

        var mergeDto = mergeRes.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<McpServerDto>().Subject;
        mergeDto.EnvMasked.Should().ContainKeys("PGPASSWORD", "PGHOST");
        mergeDto.EnvMasked.Should().NotContainKey("PGUSER");

        var entity = await db.McpServers.FindAsync("postgres");
        var decrypted = McpSecretProtector.DecryptDictionary(_encryption, entity!.EnvEncrypted);
        decrypted.Should().Equal(new Dictionary<string, string>
        {
            ["PGPASSWORD"] = "super_secret_pw_1234",
            ["PGHOST"] = "db.internal"
        });

        // 3. Clear all env vars with empty dictionary
        var clearRes = await controller.UpdateMcpServer("postgres", new UpdateMcpServerRequest(
            Env: new Dictionary<string, string>()
        ));
        var clearDto = clearRes.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<McpServerDto>().Subject;
        clearDto.HasEnv.Should().BeFalse();
        clearDto.EnvMasked.Should().BeEmpty();

        _mockClientProxy.Verify(
            c => c.SendCoreAsync("McpServerUpdated", It.Is<object?[]>(args => args.Length == 1), default),
            Times.Exactly(3));
    }

    [Fact]
    public async Task UpdateMcpServer_SwitchTransportToHttp_ValidatesAndClearsCommand()
    {
        await using var db = TestDbContextFactory.Create();
        var controller = new McpServersController(db, _encryption);

        await controller.CreateMcpServer(new CreateMcpServerRequest(
            Name: "Switchable",
            Id: "switchable",
            Transport: McpTransportType.Stdio,
            Command: "npx",
            Args: new List<string> { "-y", "pkg" }
        ));

        var badSwitch = await controller.UpdateMcpServer("switchable", new UpdateMcpServerRequest(
            Transport: McpTransportType.Http
        ));
        badSwitch.Should().BeOfType<BadRequestObjectResult>();

        var goodSwitch = await controller.UpdateMcpServer("switchable", new UpdateMcpServerRequest(
            Transport: McpTransportType.Http,
            Url: "https://mcp.example.com/http"
        ));

        var dto = goodSwitch.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<McpServerDto>().Subject;
        dto.Transport.Should().Be(McpTransportType.Http);
        dto.Command.Should().BeNull();
        dto.Args.Should().BeEmpty();
        dto.Url.Should().Be("https://mcp.example.com/http");
    }

    [Fact]
    public async Task DeleteMcpServer_RemovesAssignmentsAndBroadcasts()
    {
        await using var db = TestDbContextFactory.Create();
        var controller = new McpServersController(db, _encryption, _mockHubContext.Object);

        await controller.CreateMcpServer(new CreateMcpServerRequest(
            Name: "Temp Server",
            Id: "temp-srv",
            Transport: McpTransportType.Sse,
            Url: "https://temp.example.com/sse"
        ));
        db.Personas.Add(new Persona { Id = "astra", Name = "Astra", DriverId = "agy" });
        db.PersonaMcpServers.Add(new PersonaMcpServer { PersonaId = "astra", McpServerId = "temp-srv" });
        await db.SaveChangesAsync();

        var delRes = await controller.DeleteMcpServer("temp-srv");
        delRes.Should().BeOfType<OkObjectResult>();

        (await db.McpServers.FindAsync("temp-srv")).Should().BeNull();
        db.PersonaMcpServers.Where(pm => pm.McpServerId == "temp-srv").Should().BeEmpty();

        _mockClientProxy.Verify(
            c => c.SendCoreAsync("McpServerDeleted", It.Is<object?[]>(args => args.Length == 1), default),
            Times.Once);

        var missingRes = await controller.DeleteMcpServer("temp-srv");
        missingRes.Should().BeOfType<NotFoundObjectResult>();
    }
}
