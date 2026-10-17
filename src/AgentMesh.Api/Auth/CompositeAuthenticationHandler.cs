using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentMesh.Api.Auth;

public class CompositeAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // 1. Check for Bearer token in Authorization header or WebSocket query string
        var authHeader = Request.Headers.Authorization.ToString();
        var hasBearerHeader = !string.IsNullOrWhiteSpace(authHeader) &&
                              authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

        var hasAccessTokenQuery = Request.Query.ContainsKey("access_token") &&
                                  Request.Path.StartsWithSegments("/hubs");

        if (hasBearerHeader)
        {
            var bearerToken = authHeader["Bearer ".Length..].Trim();

            // If token starts with mesh_tok_, route to ApiKeyScheme
            if (bearerToken.StartsWith("mesh_tok_", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogDebug("Agent mesh token detected in Bearer header. Delegating to ApiKeyScheme.");
                return await Context.AuthenticateAsync(AuthConstants.ApiKeyScheme);
            }

            // Check if JwtBearerScheme is registered in the service container
            var schemeProvider = Context.RequestServices.GetService<IAuthenticationSchemeProvider>();
            var hasJwtScheme = schemeProvider != null && await schemeProvider.GetSchemeAsync(AuthConstants.JwtBearerScheme) != null;

            if (hasJwtScheme)
            {
                Logger.LogDebug("Bearer credentials detected. Delegating to JwtBearerScheme.");
                var jwtResult = await Context.AuthenticateAsync(AuthConstants.JwtBearerScheme);
                if (jwtResult.Succeeded)
                {
                    return jwtResult;
                }
            }

            // Fallback to ApiKeyScheme in case the Bearer token was a master dev key or agent key
            var apiKeyResult = await Context.AuthenticateAsync(AuthConstants.ApiKeyScheme);
            if (apiKeyResult.Succeeded)
            {
                return apiKeyResult;
            }

            return AuthenticateResult.Fail("Invalid Bearer credentials.");
        }

        if (hasAccessTokenQuery)
        {
            var queryToken = Request.Query["access_token"].ToString().Trim();
            if (queryToken.StartsWith("mesh_tok_", StringComparison.OrdinalIgnoreCase))
            {
                return await Context.AuthenticateAsync(AuthConstants.ApiKeyScheme);
            }

            var schemeProvider = Context.RequestServices.GetService<IAuthenticationSchemeProvider>();
            var hasJwtScheme = schemeProvider != null && await schemeProvider.GetSchemeAsync(AuthConstants.JwtBearerScheme) != null;
            if (hasJwtScheme)
            {
                var jwtResult = await Context.AuthenticateAsync(AuthConstants.JwtBearerScheme);
                if (jwtResult.Succeeded)
                {
                    return jwtResult;
                }
            }

            return await Context.AuthenticateAsync(AuthConstants.ApiKeyScheme);
        }

        // 2. Check for X-Agent-Key or X-Api-Key headers
        if (Request.Headers.ContainsKey("X-Agent-Key") || Request.Headers.ContainsKey("X-Api-Key"))
        {
            Logger.LogDebug("API/Agent Key header detected. Delegating to ApiKeyScheme.");
            return await Context.AuthenticateAsync(AuthConstants.ApiKeyScheme);
        }

        // 3. No credentials presented
        return AuthenticateResult.NoResult();
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.Append("WWW-Authenticate", "ApiKey realm=\"AgentMesh\"");
        Response.Headers.Append("WWW-Authenticate", "Bearer error=\"invalid_token\", error_description=\"OIDC access token or agent token required\"");
        await Task.CompletedTask;
    }
}
