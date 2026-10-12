using Microsoft.AspNetCore.Authentication;

namespace AgentMesh.Api.Auth;

public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string DefaultScheme = "ApiKey";
    public string HeaderName { get; set; } = "X-Api-Key";
    public string ConfiguredKey { get; set; } = string.Empty;
}
