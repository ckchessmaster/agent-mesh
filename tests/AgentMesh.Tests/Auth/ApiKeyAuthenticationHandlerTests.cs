using System.Text.Encodings.Web;
using AgentMesh.Api.Auth;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Auth;

public class ApiKeyAuthenticationHandlerTests
{
    private readonly string _staticKey = "test-secret-key-123";
    private readonly IConfiguration _config;
    private readonly AuthenticationScheme _scheme;
    private readonly IOptionsMonitor<ApiKeyAuthenticationOptions> _optionsMonitor;
    private readonly IApiKeyService _apiKeyService = new ApiKeyService();

    public ApiKeyAuthenticationHandlerTests()
    {
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:StaticApiKey"] = _staticKey
            })
            .Build();

        _scheme = new AuthenticationScheme(ApiKeyAuthenticationOptions.DefaultScheme, null, typeof(ApiKeyAuthenticationHandler));

        var optMonitor = new Mock<IOptionsMonitor<ApiKeyAuthenticationOptions>>();
        optMonitor.Setup(m => m.Get(It.IsAny<string>())).Returns(new ApiKeyAuthenticationOptions());
        _optionsMonitor = optMonitor.Object;
    }

    [Fact]
    public async Task AuthenticateAsync_WithValidMasterBearerToken_Succeeds()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(new Agent
        {
            Id = "gemini-spark",
            Name = "Gemini Spark",
            TrustTier = TrustTier.SeniorManager,
            RuntimeType = RuntimeType.McpCloud
        });
        await db.SaveChangesAsync();

        var context = new DefaultHttpContext();
        context.Request.Headers["Authorization"] = $"Bearer {_staticKey}";
        context.Request.Headers["X-Agent-Id"] = "gemini-spark";

        var handler = new ApiKeyAuthenticationHandler(
            _optionsMonitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            db,
            _apiKeyService,
            _config);

        await handler.InitializeAsync(_scheme, context);

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeTrue();
        result.Principal.Should().NotBeNull();
        result.Principal!.FindFirst("AgentId")?.Value.Should().Be("gemini-spark");
        result.Principal!.FindFirst("TrustTier")?.Value.Should().Be("2");
    }

    [Fact]
    public async Task AuthenticateAsync_WithAgentSpecificToken_Succeeds()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var rawToken = "mesh_tok_test_secret_agent_token_999";
        var hashedToken = _apiKeyService.HashToken(rawToken);

        db.Agents.Add(new Agent
        {
            Id = "agy",
            Name = "Antigravity CLI",
            TrustTier = TrustTier.Intern,
            RuntimeType = RuntimeType.CliAgy,
            ApiKeyHash = hashedToken
        });
        await db.SaveChangesAsync();

        var context = new DefaultHttpContext();
        context.Request.Headers["Authorization"] = $"Bearer {rawToken}";

        var handler = new ApiKeyAuthenticationHandler(
            _optionsMonitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            db,
            _apiKeyService,
            _config);

        await handler.InitializeAsync(_scheme, context);

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeTrue();
        result.Principal.Should().NotBeNull();
        result.Principal!.FindFirst("AgentId")?.Value.Should().Be("agy");
        result.Principal!.Identity?.Name.Should().Be("Antigravity CLI");
        result.Principal!.IsInRole("Agent").Should().BeTrue();
        result.Principal!.IsInRole("Driver").Should().BeTrue();
    }

    [Fact]
    public async Task AuthenticateAsync_WithAgentMeshTokenConfig_Succeeds()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var clusterToken = "mesh_tok_cluster_master_999";
        var configWithAgentMeshToken = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AGENTMESH_TOKEN"] = clusterToken
            })
            .Build();

        var context = new DefaultHttpContext();
        context.Request.Headers["Authorization"] = $"Bearer {clusterToken}";
        context.Request.Headers["X-Agent-Id"] = "astra-driver";

        var handler = new ApiKeyAuthenticationHandler(
            _optionsMonitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            db,
            _apiKeyService,
            configWithAgentMeshToken);

        await handler.InitializeAsync(_scheme, context);

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeTrue();
        result.Principal.Should().NotBeNull();
        result.Principal!.FindFirst("AgentId")?.Value.Should().Be("astra-driver");
        result.Principal!.IsInRole("Admin").Should().BeTrue();
        result.Principal!.IsInRole("Agent").Should().BeTrue();
    }

    [Fact]
    public async Task AuthenticateAsync_WithCustomXApiKeyHeader_Succeeds()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Api-Key"] = _staticKey;

        var handler = new ApiKeyAuthenticationHandler(
            _optionsMonitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            db,
            _apiKeyService,
            _config);

        await handler.InitializeAsync(_scheme, context);

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeTrue();
        result.Principal!.FindFirst("AgentId")?.Value.Should().Be("agy");
    }

    [Fact]
    public async Task AuthenticateAsync_WithCustomXAgentIdHeader_ResolvesSpecifiedAgent()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        db.Agents.Add(new Agent
        {
            Id = "custom-ops-agent",
            Name = "Custom Ops Agent",
            TrustTier = TrustTier.LeadHuman,
            RuntimeType = RuntimeType.CliAgy
        });
        await db.SaveChangesAsync();

        var context = new DefaultHttpContext();
        context.Request.Headers["Authorization"] = $"Bearer {_staticKey}";
        context.Request.Headers["X-Agent-Id"] = "custom-ops-agent";

        var handler = new ApiKeyAuthenticationHandler(
            _optionsMonitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            db,
            _apiKeyService,
            _config);

        await handler.InitializeAsync(_scheme, context);

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeTrue();
        result.Principal!.FindFirst("AgentId")?.Value.Should().Be("custom-ops-agent");
        result.Principal!.FindFirst("TrustTier")?.Value.Should().Be("3");
    }

    [Fact]
    public async Task AuthenticateAsync_WithInvalidKey_Fails()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var context = new DefaultHttpContext();
        context.Request.Headers["Authorization"] = "Bearer wrong-secret-token";

        var handler = new ApiKeyAuthenticationHandler(
            _optionsMonitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            db,
            _apiKeyService,
            _config);

        await handler.InitializeAsync(_scheme, context);

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.Should().BeFalse();
        result.Failure?.Message.Should().Contain("Invalid or revoked");
    }

    [Fact]
    public async Task AuthenticateAsync_WithMissingKey_ReturnsNoResult()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var context = new DefaultHttpContext();

        var handler = new ApiKeyAuthenticationHandler(
            _optionsMonitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            db,
            _apiKeyService,
            _config);

        await handler.InitializeAsync(_scheme, context);

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.None.Should().BeTrue();
    }
}
