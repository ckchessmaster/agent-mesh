using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMesh.Core.Models;

namespace AgentMesh.Api.Services;

/// <summary>
/// A driver's capability blob, read out of the shape the dashboard needs.
/// </summary>
/// <remarks>
/// Deliberately does not carry the API key or raw sidecar auth key. The blob stores it in plaintext at rest today; the
/// dashboard only ever needs to know whether one is set so it can offer "leave unchanged".
/// Surfacing the value here would put a secret into the browser bundle and the React tree.
/// </remarks>
public record DriverCapabilitiesView(
    string? Adapter,
    string? Endpoint,
    string? DefaultModel,
    int? TimeoutSeconds,
    int? MaxRetries,
    int? RetryDelayMilliseconds,
    int? MaxSteps,
    IReadOnlyList<string> Capabilities,
    bool HasApiKey,
    DriverSidecarConfig? Sidecar = null,
    bool HasSidecarAuthKey = false,
    string? LoginCommand = null,
    string? AuthCheckCommand = null)
{
    public static readonly DriverCapabilitiesView Empty =
        new(null, null, null, null, null, null, null, Array.Empty<string>(), false, null, false, null, null);
}

/// <summary>
/// Read/merge helpers for <c>Agent.CapabilitiesJson</c>.
///
/// Why this exists: the blob has two authors with different knowledge. The CLI serializes its
/// entire local preset into it (adapter, endpoint, apiKey, model, limits, capability list) and
/// re-pushes the whole thing on every registration. The dashboard historically rebuilt it from
/// the four fields its form rendered — which silently erased maxSteps, timeoutSeconds, maxRetries,
/// retryDelayMilliseconds, the capability list and the fetched model cache on every save.
///
/// The fix is to treat writes as patches: existing keys survive unless the incoming blob sets
/// them. A null in the patch means "not set", not "clear it", because the CLI emits nulls for
/// every optional it doesn't have.
/// </summary>
public static class DriverCapabilities
{
    /// <summary>
    /// Shallow-merges <paramref name="incoming"/> over <paramref name="existing"/>.
    /// Object + object merges key by key; array + array replaces; mixed shapes normalize to an
    /// object with the array moved under <c>capabilities</c> so limits are never lost.
    /// Unparseable input is never allowed to blank out stored config.
    /// </summary>
    public static string? Merge(string? existing, string? incoming)
    {
        if (string.IsNullOrWhiteSpace(incoming)) return existing;

        JsonNode? incomingNode;
        try
        {
            incomingNode = JsonNode.Parse(incoming);
        }
        catch (JsonException)
        {
            // Garbage in must not overwrite a working config.
            return existing;
        }

        if (incomingNode is null) return existing;

        if (string.IsNullOrWhiteSpace(existing)) return incomingNode.ToJsonString();

        JsonNode? existingNode;
        try
        {
            existingNode = JsonNode.Parse(existing);
        }
        catch (JsonException)
        {
            return incomingNode.ToJsonString();
        }

        if (existingNode is null) return incomingNode.ToJsonString();

        // Both plain capability lists: the patch is the whole truth about capabilities.
        if (existingNode is JsonArray existingArray && incomingNode is JsonArray incomingArray)
        {
            return incomingArray.DeepClone().ToJsonString();
        }

        var existingObj = AsObject(existingNode);
        var incomingObj = AsObject(incomingNode);

        foreach (var (key, value) in incomingObj.AsObject())
        {
            if (value is null) continue; // absent, not cleared

            if (key.Equals("sidecar", StringComparison.OrdinalIgnoreCase) &&
                value is JsonObject incomingSidecar &&
                existingObj[key] is JsonObject existingSidecar)
            {
                var mergedSidecar = existingSidecar.DeepClone().AsObject();
                foreach (var (sKey, sVal) in incomingSidecar)
                {
                    if (sVal is null) continue;
                    if (sKey.Equals("authKey", StringComparison.OrdinalIgnoreCase) &&
                        string.IsNullOrWhiteSpace(sVal.GetValue<string?>()))
                    {
                        continue;
                    }
                    mergedSidecar[sKey] = sVal.DeepClone();
                }
                existingObj[key] = mergedSidecar;
                continue;
            }

            existingObj[key] = value.DeepClone();
        }

        return existingObj.ToJsonString();
    }

    /// <summary>
    /// Lifts a bare capability array into the object envelope under <c>capabilities</c>,
    /// leaving objects (and non-collection nodes) alone.
    /// </summary>
    private static JsonObject AsObject(JsonNode node)
    {
        if (node is JsonObject obj) return obj;

        var wrapped = new JsonObject();
        if (node is JsonArray arr)
        {
            wrapped["capabilities"] = arr.DeepClone();
        }
        return wrapped;
    }

    public static DriverCapabilitiesView Parse(string? capabilitiesJson)
    {
        if (string.IsNullOrWhiteSpace(capabilitiesJson)) return DriverCapabilitiesView.Empty;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(capabilitiesJson);
        }
        catch (JsonException)
        {
            return DriverCapabilitiesView.Empty;
        }

        if (node is null) return DriverCapabilitiesView.Empty;

        if (node is JsonArray bareArray)
        {
            return DriverCapabilitiesView.Empty with { Capabilities = ReadStringArray(bareArray) };
        }

        if (node is not JsonObject obj) return DriverCapabilitiesView.Empty;

        var capabilities = obj["capabilities"] is JsonArray capsArray
            ? ReadStringArray(capsArray)
            : Array.Empty<string>();

        DriverSidecarConfig? sidecar = null;
        var hasSidecarAuthKey = false;
        if (obj.TryGetPropertyValue("sidecar", out var sidecarNode) && sidecarNode is JsonObject sidecarObj)
        {
            try
            {
                sidecar = JsonSerializer.Deserialize<DriverSidecarConfig>(sidecarObj.ToJsonString());
                if (sidecar != null)
                {
                    hasSidecarAuthKey = !string.IsNullOrWhiteSpace(sidecar.AuthKey);
                    sidecar.AuthKey = null; // Redact sensitive auth key from dashboard view
                }
            }
            catch { }
        }

        return new DriverCapabilitiesView(
            Adapter: ReadString(obj, "adapter"),
            Endpoint: ReadString(obj, "endpoint"),
            DefaultModel: ReadString(obj, "defaultModel") ?? ReadString(obj, "model"),
            TimeoutSeconds: ReadInt(obj, "timeoutSeconds"),
            MaxRetries: ReadInt(obj, "maxRetries"),
            RetryDelayMilliseconds: ReadInt(obj, "retryDelayMilliseconds"),
            MaxSteps: ReadInt(obj, "maxSteps"),
            Capabilities: capabilities,
            HasApiKey: !string.IsNullOrWhiteSpace(ReadString(obj, "apiKey")),
            Sidecar: sidecar,
            HasSidecarAuthKey: hasSidecarAuthKey,
            LoginCommand: ReadString(obj, "loginCommand") ?? sidecar?.LoginCommand,
            AuthCheckCommand: ReadString(obj, "authCheckCommand") ?? sidecar?.AuthCheckCommand);
    }

    private static IReadOnlyList<string> ReadStringArray(JsonArray array)
    {
        var values = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
            {
                values.Add(s);
            }
        }
        return values;
    }

    private static string? ReadString(JsonObject obj, string key)
    {
        if (obj.TryGetPropertyValue(key, out var node) && node is JsonValue value &&
            value.TryGetValue<string>(out var s))
        {
            return s;
        }
        return null;
    }

    private static int? ReadInt(JsonObject obj, string key)
    {
        if (obj.TryGetPropertyValue(key, out var node) && node is JsonValue value)
        {
            // Numbers may arrive as int, long or double depending on who wrote them.
            if (value.TryGetValue<int>(out var i)) return i;
            if (value.TryGetValue<long>(out var l) && l is >= int.MinValue and <= int.MaxValue) return (int)l;
            if (value.TryGetValue<double>(out var d) && d is >= int.MinValue and <= int.MaxValue) return (int)d;
        }
        return null;
    }
}
