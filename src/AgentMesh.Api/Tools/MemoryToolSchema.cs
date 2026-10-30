using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMesh.Core.Models;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentMesh.Api.Tools;

/// <summary>
/// Publishes the <c>category</c> argument of <c>save_memory</c> as a closed JSON-schema enum.
/// </summary>
/// <remarks>
/// The tool takes <c>category</c> as a string so it can answer a bad value with the allowed list instead
/// of the library's generic invocation error (see <see cref="MemoryTools"/>). That trades away the schema
/// constraint, and the schema is what a capable client validates against before it ever sends a request —
/// so without this, we would be relying on the model to read a description rather than on a contract.
///
/// The filter rewrites the generated schema at <c>tools/list</c> time from
/// <see cref="AgentMemoryCategories.All"/>, the same list the tool validates against. There is exactly one
/// place the buckets are spelled, and the wire contract is derived from it.
/// </remarks>
public static class MemoryToolSchema
{
    /// <summary>The tool whose <c>category</c> argument carries the enum constraint.</summary>
    public const string SaveMemoryToolName = "save_memory";

    /// <summary>
    /// Adds the <c>enum</c> keyword to <c>save_memory</c>'s <c>category</c> property on a
    /// <c>tools/list</c> result, mutating the tools in place.
    /// </summary>
    /// <remarks>
    /// Tolerates a null result: the request-filter delegate can hand back null when no tool collection
    /// was resolved, and the filter must not be the thing that turns that into a null-reference crash.
    /// </remarks>
    public static void ApplyCategoryEnum(ListToolsResult? result)
    {
        if (result?.Tools is null)
        {
            return;
        }

        foreach (var tool in result.Tools)
        {
            if (tool is null || !string.Equals(tool.Name, SaveMemoryToolName, StringComparison.Ordinal))
            {
                continue;
            }

            if (TryBuildConstrainedSchema(tool.InputSchema, out var constrained))
            {
                tool.InputSchema = constrained;
            }
        }
    }

    /// <summary>
    /// Returns <paramref name="schema"/> with <c>properties.category.enum</c> set, or false when the
    /// schema does not have the shape this filter expects.
    /// </summary>
    /// <remarks>
    /// Deliberately non-throwing. This runs on every <c>tools/list</c>, so a signature change in
    /// <c>MemoryTools</c> must degrade to "the enum is not advertised" (the tool still validates and
    /// still returns the allowed list) rather than take the whole MCP endpoint down.
    /// </remarks>
    internal static bool TryBuildConstrainedSchema(JsonElement schema, out JsonElement constrained)
    {
        constrained = default;

        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object ||
            !properties.TryGetProperty("category", out var category) ||
            category.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        try
        {
            var root = JsonSerializer.SerializeToNode(schema)?.AsObject();
            var categoryNode = root?["properties"]?["category"]?.AsObject();
            if (categoryNode is null)
            {
                return false;
            }

            categoryNode["enum"] = new JsonArray([
                .. AgentMemoryCategories.All.Select(value => (JsonNode?)JsonValue.Create(value))
            ]);

            constrained = JsonSerializer.Deserialize<JsonElement>(root!.ToJsonString());
            return true;
        }
        catch (InvalidOperationException)
        {
            // SerializeToNode returns null for a JSON null input; AsObject() throws on a non-object.
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
