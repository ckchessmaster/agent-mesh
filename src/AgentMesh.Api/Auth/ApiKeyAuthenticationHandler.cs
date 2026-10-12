using System.Security.Claims;
using System.Text.Encodings.Web;
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
    IConfiguration configuration)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // 1. Extract Token from Authorization: Bearer <key> or X-Api-Key
        string? providedKey = null;
        if (Request.Headers.TryGetValue("Authorization", out var authHeader) &&
            authHeader.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            providedKey = authHeader.ToString()["Bearer ".Length..].Trim();
        }
        else if (Request.Headers.TryGetValue(Options.HeaderName, out var customHeader))
        {
            providedKey = customHeader.ToString();
        }

        var masterKey = configuration["Auth:StaticApiKey"] ?? "mesh-default-secret-key";
        if (string.IsNullOrEmpty(providedKey) || providedKey != masterKey)
        {
            return AuthenticateResult.Fail("Invalid or missing API Key.");
        }

        // 2. Identify Agent via X-Agent-Id header (defaults to gemini-spark if omitted)
        var agentId = Request.Headers.TryGetValue("X-Agent-Id", out var idHeader)
            ? idHeader.ToString()
            : "gemini-spark";

        var agent = await dbContext.Agents.FirstOrDefaultAsync(a => a.Id == agentId);
        var trustTier = agent != null ? ((int)agent.TrustTier).ToString() : "1";

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, agentId),
            new Claim("AgentId", agentId),
            new Claim("TrustTier", trustTier)
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}
