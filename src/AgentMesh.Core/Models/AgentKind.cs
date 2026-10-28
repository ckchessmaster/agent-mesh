using System.Text.Json;

namespace AgentMesh.Core.Models;

/// <summary>
/// Distinguishes a real driver (a runtime the dashboard should list and let you start sessions
/// on) from a *persona runner identity*: the row the persona container manager mints purely so a
/// persona's API token has something to authenticate against.
/// </summary>
/// <remarks>
/// Why the second kind exists at all: <c>ApiKeyAuthenticationHandler</c> resolves a bearer token
/// by hashing it and matching <c>agents.api_key_hash</c>. A persona therefore needs a row in
/// <c>agents</c> to call the API, even though it is not a driver.
///
/// Why it must not be listed as a driver: the row is created with the persona's name and the
/// persona's role, so the drivers tab shows every driver twice (once as the driver, once as the
/// persona) and labels the copy with a job description that belongs to a persona.
///
/// Detection is deliberately two-tier:
///   * explicit  - the <c>role</c> marker in the capabilities blob, written by current code;
///   * heuristic - the row's id is a persona id whose own driver is a different row. This covers
///                 rows created before the marker existed, so no data migration is required.
/// </remarks>
public static class AgentKind
{
    /// <summary>Capabilities marker identifying a persona runner identity row.</summary>
    public const string PersonaRunnerRole = "persona-runner";

    /// <summary>Neutral role carried by driver rows. `role` is a persona concept, not a driver one.</summary>
    public const string DriverRole = "Driver";

    /// <summary>
    /// True when <paramref name="agent"/> is a persona's auth identity rather than a driver.
    /// </summary>
    /// <param name="agent">The agent row to classify.</param>
    /// <param name="personaIdsById">Persona id -> owning driver id. Pass an empty map to use only the explicit marker.</param>
    public static bool IsPersonaRunnerIdentity(Agent agent, IReadOnlyDictionary<string, string> personaIdsById)
    {
        if (agent == null) return false;

        if (HasRunnerMarker(agent.CapabilitiesJson)) return true;

        // Legacy rows carry no marker. A row whose id is also a persona id is a runner identity,
        // unless that persona's driver really is this same id (a persona running directly on a
        // driver of the same name is a legitimate single-row setup).
        var id = agent.Id?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(id)) return false;

        return personaIdsById.TryGetValue(id, out var owningDriver) &&
               !string.Equals(owningDriver?.Trim().ToLowerInvariant(), id, StringComparison.Ordinal);
    }

    /// <summary>True when the capabilities blob carries the explicit runner marker.</summary>
    public static bool HasRunnerMarker(string? capabilitiesJson)
    {
        if (string.IsNullOrWhiteSpace(capabilitiesJson)) return false;

        try
        {
            using var doc = JsonDocument.Parse(capabilitiesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            return TryReadRole(doc.RootElement) == PersonaRunnerRole;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the capabilities blob for a freshly minted runner identity. Existing keys are kept
    /// so a row that is promoted to a real driver does not lose its config.
    /// </summary>
    public static string BuildRunnerIdentityCapabilities(string personaId, string? driverId, string? existingJson)
    {
        var persona = personaId.Trim().TrimStart('@').ToLowerInvariant();
        var driver = driverId?.Trim().TrimStart('@').ToLowerInvariant();

        var patch = $"{{\"role\":\"{PersonaRunnerRole}\",\"personaId\":\"{Escape(persona)}\"";
        if (!string.IsNullOrEmpty(driver))
        {
            patch += $",\"driverId\":\"{Escape(driver)}\"";
        }
        patch += "}";

        return MergePatch(existingJson, patch);
    }

    private static string? TryReadRole(JsonElement root)
    {
        if (root.TryGetProperty("role", out var role) && role.ValueKind == JsonValueKind.String)
        {
            return role.GetString()?.Trim().ToLowerInvariant();
        }

        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, "role", StringComparison.OrdinalIgnoreCase) &&
                prop.Value.ValueKind == JsonValueKind.String)
            {
                return prop.Value.GetString()?.Trim().ToLowerInvariant();
            }
        }

        return null;
    }

    private static string MergePatch(string? existingJson, string patchJson)
    {
        Dictionary<string, JsonElement> merged;
        try
        {
            merged = ParseExistingAsObject(existingJson);
        }
        catch (JsonException)
        {
            // A corrupt blob must not keep the runner identity unmarked.
            return patchJson;
        }

        using var patchDoc = JsonDocument.Parse(patchJson);
        foreach (var prop in patchDoc.RootElement.EnumerateObject())
        {
            merged[prop.Name] = prop.Value.Clone();
        }

        return JsonSerializer.Serialize(merged);
    }

    /// <summary>
    /// Reads the stored blob as an object, relocating a bare capability array under
    /// <c>capabilities</c> so the marker can be added without destroying it.
    /// </summary>
    /// <remarks>
    /// <c>Agent.CapabilitiesJson</c> defaults to <c>"[]"</c> and <c>DriverCapabilities.Parse</c>
    /// accepts a bare array as the capability list, so an array blob is live data. Deserialising
    /// it into a dictionary throws, and the previous fallback returned a patch-only blob - which
    /// silently deleted the row's declared capabilities on the first token sync.
    /// Mirrors how <c>DriverCapabilities.Merge</c> normalises the mixed shape, so the two writers
    /// cannot disagree about where the list lives.
    /// </remarks>
    private static Dictionary<string, JsonElement> ParseExistingAsObject(string? existingJson)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(existingJson)) return result;

        using var doc = JsonDocument.Parse(existingJson);

        switch (doc.RootElement.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    result[prop.Name] = prop.Value.Clone();
                }

                return result;

            case JsonValueKind.Array:
                // An empty array carries nothing, so it is simply dropped.
                if (doc.RootElement.GetArrayLength() > 0)
                {
                    result["capabilities"] = doc.RootElement.Clone();
                }

                return result;

            default:
                // A scalar (or null) blob is not a config object; the marker patch wins.
                return result;
        }
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
