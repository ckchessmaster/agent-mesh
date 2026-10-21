using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentMesh.Api.Controllers;

public record AuthVerifyResponse(
    bool Authenticated,
    string? Email,
    string? Name,
    string? AgentId,
    string? TrustTier
);

[ApiController]
[Route("api/auth")]
[Authorize]
public class AuthController : ControllerBase
{
    [HttpGet("verify")]
    public IActionResult Verify()
    {
        var identity = User.Identity;
        if (identity == null || !identity.IsAuthenticated)
        {
            return Unauthorized();
        }

        var email = User.FindFirst(ClaimTypes.Email)?.Value
            ?? User.FindFirst("email")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? "admin@agentmesh.local";

        var name = User.FindFirst(ClaimTypes.Name)?.Value
            ?? User.FindFirst("name")?.Value
            ?? email;

        var agentId = User.FindFirst("AgentId")?.Value ?? "operator";
        var trustTier = User.FindFirst("TrustTier")?.Value ?? "3";

        return Ok(new AuthVerifyResponse(
            Authenticated: true,
            Email: email,
            Name: name,
            AgentId: agentId,
            TrustTier: trustTier
        ));
    }
}
