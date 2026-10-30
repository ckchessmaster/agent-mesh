using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMesh.Core.Models;
using ModelContextProtocol.Protocol;

namespace AgentMesh.Api.Tools;

/// <summary>
/// Publishes the closed argument vocabularies of the platform tools on their <c>tools/list</c>
/// schemas.
/// </summary>
/// <remarks>
/// <para>
/// <c>manage_task</c> takes <c>action</c> and <c>status</c> as strings so a bad value gets an
/// actionable error naming the allowed values, rather than the library's generic
/// "An error occurred invoking 'manage_task'". That is the same trade-off already documented on
/// <c>save_memory</c>'s <c>category</c> parameter, and it costs the same thing: the generated
/// schema loses its <c>enum</c>.
/// </para>
/// <para>
/// The schema is what a capable client validates against <em>before</em> it sends a request, so
/// leaving the constraint off would mean relying on a model to read a description rather than on a
/// contract. This filter puts both enums back, derived from the same lists the handlers validate
/// against — <see cref="JobTaskStatus.All"/> and the action constants on
/// <see cref="PlatformMcpTools"/>. There is exactly one place each vocabulary is spelled, and the
/// wire contract is generated from it.
/// </para>
/// <para>
/// <strong>Why <c>post_job_message.target_persona</strong> gets no enum.</strong> The job spec
/// lists <c>["operator", "astra", "sorren", "vane"]</c>. That is a fixed four-value set, and the
/// real answer is per-project: the valid targets are whoever is assigned to the job's project,
/// which is data in <c>project_personas</c> and differs between deployments. Advertising a closed
/// enum would tell a client that <c>"vane"</c> is acceptable on a project that has no security
/// persona — the exact call the handler then refuses — and would exclude a team's real personas.
/// The handler validates against the assignment table and returns the actual list on refusal, which
/// is both correct and self-correcting; a hardcoded enum would be neither.
/// </para>
/// </remarks>
public static class PlatformToolSchema
{
    internal const string ManageTaskToolName = "manage_task";

    /// <summary>
    /// The <c>action</c> values <c>manage_task</c> accepts.
    /// </summary>
    /// <remarks>
    /// Public and referenced by the handler's own switch, so the advertised enum and the accepted
    /// set cannot drift. The handler compares against these constants rather than inline literals.
    /// </remarks>
    public const string ActionGetDetails = "get_details";
    public const string ActionSetStatus = "set_status";
    public const string ActionListAll = "list_all";

    public static readonly IReadOnlyList<string> AllActions = [ActionGetDetails, ActionSetStatus, ActionListAll];

    /// <summary>
    /// Adds the <c>enum</c> keywords to <c>manage_task</c>'s arguments on a <c>tools/list</c>
    /// result, mutating the tools in place.
    /// </summary>
    /// <remarks>
    /// Tolerates a null result: the request-filter delegate can hand back null when no tool
    /// collection was resolved, and the filter must not be the thing that turns that into a
    /// null-reference crash on every tool discovery request.
    /// </remarks>
    public static void ApplyManagedTaskEnums(ListToolsResult? result)
    {
        if (result?.Tools is null)
        {
            return;
        }

        foreach (var tool in result.Tools)
        {
            if (tool is null || !string.Equals(tool.Name, ManageTaskToolName, StringComparison.Ordinal))
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
    /// Returns <paramref name="schema"/> with <c>properties.action.enum</c> and
    /// <c>properties.status.enum</c> set, or false when the schema is not the shape expected.
    /// </summary>
    /// <remarks>
    /// Deliberately non-throwing, and deliberately partial: if one of the two properties is missing
    /// or has changed type, the other is still patched. This runs on every <c>tools/list</c>, so a
    /// signature change in <c>PlatformMcpTools</c> must degrade to "one enum is not advertised" —
    /// the handler still validates and still returns the allowed list — rather than take the whole
    /// MCP endpoint down.
    /// </remarks>
    internal static bool TryBuildConstrainedSchema(JsonElement schema, out JsonElement constrained)
    {
        constrained = default;

        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        try
        {
            var root = JsonSerializer.SerializeToNode(schema)?.AsObject();
            var propertiesNode = root?["properties"]?.AsObject();
            if (propertiesNode is null)
            {
                return false;
            }

            var patched = false;
            patched |= TryAddEnum(propertiesNode, "action", AllActions);
            patched |= TryAddEnum(propertiesNode, "status", JobTaskStatus.All);

            if (!patched)
            {
                return false;
            }

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

    private static bool TryAddEnum(JsonObject properties, string name, IReadOnlyList<string> values)
    {
        if (properties[name] is not JsonObject node)
        {
            // Absent, or not an object (a renamed parameter, or a schema the generator emitted as
            // a boolean). Leave it alone; the handler's own validation is the backstop.
            return false;
        }

        node["enum"] = new JsonArray([
            .. values.Select(value => (JsonNode?)JsonValue.Create(value))
        ]);

        return true;
    }
}
