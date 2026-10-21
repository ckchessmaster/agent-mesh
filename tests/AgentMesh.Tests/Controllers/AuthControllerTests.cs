using System.Security.Claims;
using AgentMesh.Api.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentMesh.Tests.Controllers;

public class AuthControllerTests
{
    [Fact]
    public void Verify_ReturnsOk_WhenUserIsAuthenticated()
    {
        // Arrange
        var controller = new AuthController();
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "admin@agentmesh.local"),
            new Claim(ClaimTypes.Name, "Admin User"),
            new Claim(ClaimTypes.Email, "admin@agentmesh.local"),
            new Claim("AgentId", "operator"),
            new Claim("TrustTier", "3")
        };
        var identity = new ClaimsIdentity(claims, "Bearer");
        var principal = new ClaimsPrincipal(identity);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        // Act
        var result = controller.Verify();

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var okResult = result as OkObjectResult;
        okResult.Should().NotBeNull();
        var response = okResult!.Value as AuthVerifyResponse;
        response.Should().NotBeNull();
        response!.Authenticated.Should().BeTrue();
        response.Email.Should().Be("admin@agentmesh.local");
        response.Name.Should().Be("Admin User");
        response.AgentId.Should().Be("operator");
        response.TrustTier.Should().Be("3");
    }

    [Fact]
    public void Verify_ReturnsUnauthorized_WhenUserIsNotAuthenticated()
    {
        // Arrange
        var controller = new AuthController();
        var identity = new ClaimsIdentity(); // Unauthenticated identity
        var principal = new ClaimsPrincipal(identity);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        // Act
        var result = controller.Verify();

        // Assert
        result.Should().BeOfType<UnauthorizedResult>();
    }
}
