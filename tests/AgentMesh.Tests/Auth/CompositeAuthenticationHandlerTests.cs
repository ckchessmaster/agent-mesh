using System.Security.Claims;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AgentMesh.Tests.Auth;

public class CompositeAuthenticationHandlerTests
{
    private readonly IApiKeyService _apiKeyService = new ApiKeyService();
    private readonly string _staticKey = "mesh-test-master-key-777";
    private readonly IConfiguration _config;

    public CompositeAuthenticationHandlerTests()
    {
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:StaticApiKey"] = _staticKey,
                ["Oidc:Authority"] = "http://localhost:8085/dex",
                ["Oidc:ClientId"] = "agentmesh-dashboard"
            })
            .Build();
    }

    [Fact]
    public async Task AuthenticateAsync_WithMeshTokenBearer_DelegatesToApiKeyScheme_AndSucceeds()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var rawToken = "mesh_tok_composite_test_token_12345";
        var hashed = _apiKeyService.HashToken(rawToken);

        db.Agents.Add(new Agent
        {
            Id = "agy",
            Name = "Antigravity CLI",
            TrustTier = TrustTier.LeadHuman,
            RuntimeType = RuntimeType.CliAgy,
            ApiKeyHash = hashed
        });
        await db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(db);
        services.AddSingleton(_apiKeyService);
        services.AddSingleton(_config);
        services.AddAuthentication(AuthConstants.CompositeScheme)
            .AddScheme<AuthenticationSchemeOptions, CompositeAuthenticationHandler>(AuthConstants.CompositeScheme, null)
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(AuthConstants.ApiKeyScheme, null);

        var sp = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = sp };
        context.Request.Headers["Authorization"] = $"Bearer {rawToken}";

        // Act
        var result = await context.AuthenticateAsync(AuthConstants.CompositeScheme);

        // Assert
        result.Succeeded.Should().BeTrue();
        result.Principal.Should().NotBeNull();
        result.Principal!.FindFirst(ClaimTypes.NameIdentifier)?.Value.Should().Be("agy");
        result.Principal!.FindFirst("AgentId")?.Value.Should().Be("agy");
    }

    [Fact]
    public async Task AuthenticateAsync_WithMasterKeyBearer_DelegatesToApiKeyScheme_AndSucceeds()
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

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(db);
        services.AddSingleton(_apiKeyService);
        services.AddSingleton(_config);
        services.AddAuthentication(AuthConstants.CompositeScheme)
            .AddScheme<AuthenticationSchemeOptions, CompositeAuthenticationHandler>(AuthConstants.CompositeScheme, null)
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(AuthConstants.ApiKeyScheme, null);

        var sp = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = sp };
        context.Request.Headers["Authorization"] = $"Bearer {_staticKey}";
        context.Request.Headers["X-Agent-Id"] = "gemini-spark";

        // Act
        var result = await context.AuthenticateAsync(AuthConstants.CompositeScheme);

        // Assert
        result.Succeeded.Should().BeTrue();
        result.Principal.Should().NotBeNull();
        result.Principal!.FindFirst("AgentId")?.Value.Should().Be("gemini-spark");
        result.Principal!.FindFirst("IsMasterKey")?.Value.Should().Be("true");
    }

    [Fact]
    public async Task AuthenticateAsync_WithXAgentKeyHeader_DelegatesToApiKeyScheme_AndSucceeds()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var rawToken = "mesh_tok_agent_key_header_token";
        var hashed = _apiKeyService.HashToken(rawToken);

        db.Agents.Add(new Agent
        {
            Id = "agy",
            Name = "Antigravity CLI",
            TrustTier = TrustTier.LeadHuman,
            RuntimeType = RuntimeType.CliAgy,
            ApiKeyHash = hashed
        });
        await db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(db);
        services.AddSingleton(_apiKeyService);
        services.AddSingleton(_config);
        services.AddAuthentication(AuthConstants.CompositeScheme)
            .AddScheme<AuthenticationSchemeOptions, CompositeAuthenticationHandler>(AuthConstants.CompositeScheme, null)
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(AuthConstants.ApiKeyScheme, null);

        var sp = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = sp };
        context.Request.Headers["X-Agent-Key"] = rawToken;

        // Act
        var result = await context.AuthenticateAsync(AuthConstants.CompositeScheme);

        // Assert
        result.Succeeded.Should().BeTrue();
        result.Principal!.FindFirst("AgentId")?.Value.Should().Be("agy");
    }

    [Fact]
    public async Task AuthenticateAsync_WithoutCredentials_ReturnsNoResult()
    {
        // Arrange
        using var db = TestDbContextFactory.Create();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(db);
        services.AddSingleton(_apiKeyService);
        services.AddSingleton(_config);
        services.AddAuthentication(AuthConstants.CompositeScheme)
            .AddScheme<AuthenticationSchemeOptions, CompositeAuthenticationHandler>(AuthConstants.CompositeScheme, null)
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(AuthConstants.ApiKeyScheme, null);

        var sp = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = sp };

        // Act
        var result = await context.AuthenticateAsync(AuthConstants.CompositeScheme);

        // Assert
        result.None.Should().BeTrue();
    }
}
