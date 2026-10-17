using System.Security.Claims;
using System.Text.Encodings.Web;
using AgentMesh.Api.Auth;
using AgentMesh.Api.Controllers;
using AgentMesh.Api.Hubs;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Controllers;

public class SessionManagementTests
{
    private readonly IApiKeyService _apiKeyService = new ApiKeyService();
    private readonly Mock<IHubContext<AgentMeshHub>> _mockHubContext;
    private readonly Mock<IHubClients> _mockClients;
    private readonly Mock<IClientProxy> _mockClientProxy;
    private readonly Mock<IGroupManager> _mockGroups;
    private readonly IConfiguration _config;
    private readonly IOptionsMonitor<ApiKeyAuthenticationOptions> _optionsMonitor;
    private readonly AuthenticationScheme _scheme;

    public SessionManagementTests()
    {
        _mockHubContext = new Mock<IHubContext<AgentMeshHub>>();
        _mockClients = new Mock<IHubClients>();
        _mockClientProxy = new Mock<IClientProxy>();
        _mockGroups = new Mock<IGroupManager>();

        _mockClients.Setup(c => c.All).Returns(_mockClientProxy.Object);
        _mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_mockClientProxy.Object);
        _mockHubContext.Setup(h => h.Clients).Returns(_mockClients.Object);
        _mockHubContext.Setup(h => h.Groups).Returns(_mockGroups.Object);

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:StaticApiKey"] = "master-secret-key-123"
            })
            .Build();

        var options = new ApiKeyAuthenticationOptions();
        var mockMonitor = new Mock<IOptionsMonitor<ApiKeyAuthenticationOptions>>();
        mockMonitor.Setup(m => m.Get(It.IsAny<string>())).Returns(options);
        mockMonitor.Setup(m => m.CurrentValue).Returns(options);
        _optionsMonitor = mockMonitor.Object;

        _scheme = new AuthenticationScheme(
            ApiKeyAuthenticationOptions.DefaultScheme,
            ApiKeyAuthenticationOptions.DefaultScheme,
            typeof(ApiKeyAuthenticationHandler));
    }

    [Fact]
    public async Task FullFlow_RegisterAgy_AuthenticateWithToken_CreateSession_AndPostMessage()
    {
        // 1. Arrange DB
        using var db = TestDbContextFactory.Create();

        // 2. Register 'agy'
        var agentsController = new AgentsController(db, _apiKeyService);
        var regReq = new RegisterAgentRequest("agy", "Antigravity CLI", RuntimeType.CliAgy, TrustTier.LeadHuman, "Antigravity coding assistant", "{}");
        var regResult = await agentsController.RegisterAgent(regReq);

        var createdResult = regResult.Should().BeOfType<CreatedAtActionResult>().Subject;
        createdResult.Value.Should().NotBeNull();

        // Extract token
        var tokenProperty = createdResult.Value!.GetType().GetProperty("token");
        tokenProperty.Should().NotBeNull();
        var rawToken = (string)tokenProperty!.GetValue(createdResult.Value!)!;
        rawToken.Should().StartWith("mesh_tok_");

        // 3. Authenticate with Bearer token
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = $"Bearer {rawToken}";

        var authHandler = new ApiKeyAuthenticationHandler(
            _optionsMonitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            db,
            _apiKeyService,
            _config);

        await authHandler.InitializeAsync(_scheme, httpContext);
        var authResult = await authHandler.AuthenticateAsync();

        authResult.Succeeded.Should().BeTrue();
        authResult.Principal.Should().NotBeNull();
        authResult.Principal!.FindFirst(ClaimTypes.NameIdentifier)?.Value.Should().Be("agy");

        // 4. Create Session as authenticated 'agy'
        httpContext.User = authResult.Principal!;
        var sessionsController = new SessionsController(db, _mockHubContext.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        var createReq = new CreateSessionRequest("Refactor Core System", "{\"env\":\"development\"}");
        var createResult = await sessionsController.CreateSession(createReq);

        var sessionCreated = createResult.Should().BeOfType<CreatedAtActionResult>().Subject;
        var session = sessionCreated.Value.Should().BeOfType<AgentSession>().Subject;
        session.Title.Should().Be("Refactor Core System");
        session.CreatorAgentId.Should().Be("agy");
        session.Status.Should().Be("active");

        // 5. Post Message in Session
        var msgReq = new PostMessageRequest("Analysis complete. Found 3 items to optimize.");
        var msgResult = await sessionsController.PostMessage(session.Id, msgReq);

        var msgCreated = msgResult.Should().BeOfType<CreatedAtActionResult>().Subject;
        var msg = msgCreated.Value.Should().BeOfType<SessionMessageDto>().Subject;
        msg.SenderAgentId.Should().Be("agy");
        msg.Content.Should().Be("Analysis complete. Found 3 items to optimize.");

        // 6. Retrieve Session Details and Message History
        var getSessionResult = await sessionsController.GetSession(session.Id);
        var sessionOk = getSessionResult.Should().BeOfType<OkObjectResult>().Subject;
        var detail = sessionOk.Value.Should().BeOfType<SessionDetailDto>().Subject;

        detail.Id.Should().Be(session.Id);
        detail.CreatorAgentId.Should().Be("agy");
        detail.Messages.Should().HaveCount(1);
        detail.Messages[0].Content.Should().Be("Analysis complete. Found 3 items to optimize.");
        detail.Messages[0].SenderAgentId.Should().Be("agy");
    }

    [Fact]
    public async Task Authenticate_WithInvalidToken_Fails()
    {
        using var db = TestDbContextFactory.Create();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "Bearer mesh_tok_invalid_and_nonexistent_token";

        var authHandler = new ApiKeyAuthenticationHandler(
            _optionsMonitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            db,
            _apiKeyService,
            _config);

        await authHandler.InitializeAsync(_scheme, httpContext);
        var authResult = await authHandler.AuthenticateAsync();

        authResult.Succeeded.Should().BeFalse();
        authResult.Failure?.Message.Should().Contain("Invalid or revoked agent API token");
    }
}
