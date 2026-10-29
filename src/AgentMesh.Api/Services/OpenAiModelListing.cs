using System.Text.Json;

namespace AgentMesh.Api.Services;

/// <summary>
/// Shared plumbing for the OpenAI-compatible <c>GET {endpoint}/models</c> listing.
/// </summary>
/// <remarks>
/// Two callers need this: <see cref="DriverCapabilityProbe"/> (background resolution for the
/// capability picker) and <c>SystemController.TestOpenAiServer</c> (the operator pressing
/// "Test connection" in the driver form). They previously each carried their own copy of the URL
/// normalization and <c>data[].id</c> walk, which is how the two could disagree about whether a
/// server was reachable.
/// </remarks>
internal static class OpenAiModelListing
{
    /// <summary>
    /// Turns a configured endpoint into the models URL, tolerating an operator who pasted the
    /// full <c>.../models</c> path.
    /// </summary>
    internal static string ToModelsUrl(string endpoint)
    {
        var trimmed = endpoint.Trim().TrimEnd('/');
        return trimmed.EndsWith("/models", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed}/models";
    }

    /// <summary>
    /// Extracts <c>data[].id</c> from a <c>/models</c> payload. Returns an empty list for any body
    /// that is not the expected shape - a gateway returning HTML has no models, and pretending
    /// otherwise would render an empty picker as a successful probe.
    /// </summary>
    internal static IReadOnlyList<string> ParseIds(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (!TryGetPropertyCaseInsensitive(doc.RootElement, "data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var ids = new List<string>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (TryGetPropertyCaseInsensitive(item, "id", out var id) &&
                    id.ValueKind == JsonValueKind.String)
                {
                    var value = id.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) ids.Add(value!);
                }
            }

            return ids;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Case-insensitive property lookup. OpenAI-compatible servers are third-party software and
    /// some of them serialize <c>Data</c>/<c>ID</c>.
    /// </summary>
    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out value)) return true;

            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
