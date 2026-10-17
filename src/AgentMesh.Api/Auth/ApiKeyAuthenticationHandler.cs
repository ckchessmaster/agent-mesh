using System.Security.Claims;
using System.Text.Encodings.Web;
using AgentMesh.Core.Services;
using AgentMesh.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgentMesh.Api.Auth;

public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AgentMeshDbContext dbContext,
    IApiKeyService apiKeyService,
    IConfiguration configuration)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // 1. Extract Token from Authorization: Bearer <key> or X-Api-Key / X-Agent-Key
        string? providedKey = null;
        if (Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            var headerStr = authHeader.ToString();
            if (headerStr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                providedKey = headerStr["Bearer ".Length..].Trim();
            }
        }

        if (string.IsNullOrEmpty(providedKey) && Request.Headers.TryGetValue("X-Agent-Key", out var agentKeyHeader))
        {
            providedKey = agentKeyHeader.ToString().Trim();
        }

        if (string.IsNullOrEmpty(providedKey) && Request.Headers.TryGetValue(Options.HeaderName, out var customHeader))
        {
            providedKey = customHeader.ToString().Trim();
        }

        if (string.IsNullOrEmpty(providedKey))
        {
            return AuthenticateResult.NoResult();
        }

        // 2. Check Master Dev Key for backwards compatibility / local developer access
        var masterKey = configuration["Auth:StaticApiKey"] ?? "mesh-default-secret-key";
        if (providedKey == masterKey)
        {
            var devAgentId = Request.Headers.TryGetValue("X-Agent-Id", out var idHeader)
                ? idHeader.ToString()
                : "agy";

            var devAgent = await dbContext.Agents.FirstOrDefaultAsync(a => a.Id == devAgentId);
            var devTrustTier = devAgent != null ? ((int)devAgent.TrustTier).ToString() : "1";

            var devClaims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, devAgentId),
                new Claim(ClaimTypes.Name, devAgent?.Name ?? devAgentId),
                new Claim("AgentId", devAgentId),
                new Claim("TrustTier", devTrustTier),
                new Claim("IsMasterKey", "true")
            };

            var devIdentity = new ClaimsIdentity(devClaims, Scheme.Name);
            var devPrincipal = new ClaimsPrincipal(devIdentity);
            return AuthenticateResult.Success(new AuthenticationTicket(devPrincipal, Scheme.Name));
        }

        // 3. Hash Provided Token and match against Agents.ApiKeyHash in PostgreSQL
        var hashedToken = apiKeyService.HashToken(providedKey);
        var agent = await dbContext.Agents.FirstOrDefaultAsync(a => a.ApiKeyHash == hashedToken);

        if (agent == null)
        {
            return AuthenticateResult.Fail("Invalid or revoked agent API token.");
        }

        // 4. Construct Authenticated ClaimsPrincipal for the registered agent
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, agent.Id),
            new Claim(ClaimTypes.Name, agent.Name),
            new Claim("AgentId", agent.Id),
            new Claim("TrustTier", ((int)agent.TrustTier).ToString()),
            new Claim("RuntimeType", agent.RuntimeType.ToString())
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
