using System.Text.Json.Serialization;

namespace AgentMesh.Core.Models;

/// <summary>
/// OpenRouter-specific configuration persisted under <c>capabilitiesJson.openRouter</c>.
/// </summary>
public sealed class OpenRouterConfig
{
    public const string DefaultEndpoint = "https://openrouter.ai/api/v1";
    public const string DefaultAppTitle = "AgentMesh";

    [JsonPropertyName("httpReferer")]
    public string? HttpReferer { get; set; }

    [JsonPropertyName("appTitle")]
    public string? AppTitle { get; set; }

    [JsonPropertyName("fallbackModels")]
    public List<string>? FallbackModels { get; set; }

    [JsonPropertyName("provider")]
    public OpenRouterProviderRouting? Provider { get; set; }
}

/// <summary>
/// Provider routing preferences forwarded in OpenRouter's <c>provider</c> request field.
/// </summary>
public sealed class OpenRouterProviderRouting
{
    [JsonPropertyName("sort")]
    public string? Sort { get; set; }

    [JsonPropertyName("order")]
    public List<string>? Order { get; set; }

    [JsonPropertyName("only")]
    public List<string>? Only { get; set; }

    [JsonPropertyName("ignore")]
    public List<string>? Ignore { get; set; }

    [JsonPropertyName("allowFallbacks")]
    public bool? AllowFallbacks { get; set; }

    [JsonPropertyName("dataCollection")]
    public string? DataCollection { get; set; }

    [JsonPropertyName("zdr")]
    public bool? Zdr { get; set; }

    [JsonPropertyName("requireParameters")]
    public bool? RequireParameters { get; set; }
}
