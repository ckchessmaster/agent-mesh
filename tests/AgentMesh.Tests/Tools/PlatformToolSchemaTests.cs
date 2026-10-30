using System.Text.Json;
using AgentMesh.Api.Tools;
using AgentMesh.Core.Models;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AgentMesh.Tests.Tools;

/// <summary>
/// The <c>tools/list</c> filter that advertises <c>manage_task</c>'s two closed vocabularies.
/// </summary>
/// <remarks>
/// <c>PlatformMcpTools</c> deliberately takes <c>action</c> and <c>status</c> as strings so a bad
/// value gets an actionable error naming the allowed values, rather than the library's generic
/// invocation failure. That trades away the generated schema's <c>enum</c>, and this filter is what
/// puts it back. If the filter silently stops matching, the tool still works — but every client
/// loses the constraint it validates against before sending, which is a degradation no behavioural
/// test would notice. Hence a dedicated test class for a function whose failure is invisible.
/// </remarks>
public class PlatformToolSchemaTests
{
    private static Tool MakeTool(string name, string inputSchemaJson) => new()
    {
        Name = name,
        InputSchema = JsonDocument.Parse(inputSchemaJson).RootElement,
    };

    private const string ManageTaskSchema = """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "description": "Action to perform." },
            "task_id": { "type": ["string","null"], "default": null },
            "status": { "type": ["string","null"], "description": "New status." },
            "notes": { "type": ["string","null"] }
          },
          "required": ["action"]
        }
        """;

    [Fact]
    public void ApplyManagedTaskEnums_PatchesBothVocabulariesFromTheSingleSourceOfTruth()
    {
        var tool = MakeTool("manage_task", ManageTaskSchema);

        PlatformToolSchema.ApplyManagedTaskEnums(new ListToolsResult { Tools = [tool] });

        using var schema = JsonDocument.Parse(tool.InputSchema.GetRawText());
        var properties = schema.RootElement.GetProperty("properties");

        properties.GetProperty("action").GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(PlatformToolSchema.AllActions);

        properties.GetProperty("status").GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(JobTaskStatus.All);
    }

    [Fact]
    public void ApplyManagedTaskEnums_PreservesEverythingElseAboutTheProperty()
    {
        // The patch adds one keyword. Losing the description would cost the caller the explanation
        // of when to use which action, and dropping "required" would make every argument optional.
        var tool = MakeTool("manage_task", ManageTaskSchema);

        PlatformToolSchema.ApplyManagedTaskEnums(new ListToolsResult { Tools = [tool] });

        using var schema = JsonDocument.Parse(tool.InputSchema.GetRawText());
        var root = schema.RootElement;

        root.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().Equal("action");
        root.GetProperty("properties").GetProperty("action")
            .GetProperty("description").GetString().Should().Be("Action to perform.");
        root.GetProperty("properties").GetProperty("status")
            .GetProperty("description").GetString().Should().Be("New status.");
        root.GetProperty("properties").GetProperty("notes").GetProperty("type")
            .EnumerateArray().Select(e => e.GetString()).Should().Equal("string", "null");
    }

    [Fact]
    public void ApplyManagedTaskEnums_LeavesOtherToolsAlone()
    {
        // The filter runs over every tool the server publishes. Rewriting an unrelated tool's
        // schema — for instance a future 'status' parameter on another tool — would constrain it
        // to task states it has nothing to do with.
        var other = MakeTool("save_memory", """{"type":"object","properties":{"status":{"type":"string"}}}""");
        var manageTask = MakeTool("manage_task", ManageTaskSchema);

        PlatformToolSchema.ApplyManagedTaskEnums(new ListToolsResult { Tools = [other, manageTask] });

        using var otherSchema = JsonDocument.Parse(other.InputSchema.GetRawText());
        otherSchema.RootElement.GetProperty("properties").GetProperty("status")
            .TryGetProperty("enum", out _).Should().BeFalse();

        using var patched = JsonDocument.Parse(manageTask.InputSchema.GetRawText());
        patched.RootElement.GetProperty("properties").GetProperty("status").TryGetProperty("enum", out _)
            .Should().BeTrue();
    }

    [Fact]
    public void ApplyManagedTaskEnums_PatchesOnePropertyWhenTheOtherHasChangedShape()
    {
        // Partial degradation is the point: a retype of `status` must not also cost the caller the
        // `action` constraint. Both handlers still validate regardless.
        var tool = MakeTool("manage_task", """
            {"type":"object","properties":{
              "action":{"type":"string"},
              "status":true
            }}
            """);

        PlatformToolSchema.ApplyManagedTaskEnums(new ListToolsResult { Tools = [tool] });

        using var schema = JsonDocument.Parse(tool.InputSchema.GetRawText());
        var properties = schema.RootElement.GetProperty("properties");

        properties.GetProperty("action").GetProperty("enum").GetArrayLength().Should().Be(3);

        // The untouched property is still the boolean it was. Asserted on ValueKind rather than by
        // calling TryGetProperty on it, which throws on a non-object element — the JSON Schema
        // boolean form ("status": true means "accept anything") is legal and must survive as-is.
        properties.GetProperty("status").ValueKind.Should().Be(JsonValueKind.True);
    }

    [Fact]
    public void ApplyManagedTaskEnums_LeavesASchemaWithNeitherPropertyUntouched()
    {
        var tool = MakeTool("manage_task", """{"type":"object","properties":{"other":{"type":"string"}}}""");
        var before = tool.InputSchema.GetRawText();

        PlatformToolSchema.ApplyManagedTaskEnums(new ListToolsResult { Tools = [tool] });

        // Nothing was patched, so the schema must be byte-identical rather than merely equivalent:
        // a needless rewrite would churn the value every client caches.
        tool.InputSchema.GetRawText().Should().Be(before);
    }

    [Fact]
    public void TryBuildConstrainedSchema_ToleratesDegenerateSchemasWithoutThrowing()
    {
        // Called directly rather than through ApplyManagedTaskEnums: the MCP library validates
        // Tool.InputSchema on assignment, so a malformed schema cannot be constructed as a Tool at
        // all. The filter still has to survive one reaching it, because tools/list runs over every
        // tool the server publishes and a throw here takes the whole endpoint down.
        var inputs = new[]
        {
            "null",
            "[]",
            "\"a string\"",
            "5",
            "{}",
            """{"properties":5}""",
            """{"properties":null}""",
            """{"properties":[]}""",
            """{"properties":{"action":null}}""",
            """{"properties":{"action":"string"}}""",
            """{"properties":{"status":true}}""",
        };

        foreach (var input in inputs)
        {
            var schema = JsonDocument.Parse(input).RootElement;

            var act = () => PlatformToolSchema.TryBuildConstrainedSchema(schema, out _);
            act.Should().NotThrow($"the filter must never be the thing that breaks tools/list ({input})");
        }
    }

    [Fact]
    public void TryBuildConstrainedSchema_ReturnsFalseWhenThereIsNothingToPatch()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"other":{"type":"string"}}}""").RootElement;

        PlatformToolSchema.TryBuildConstrainedSchema(schema, out _).Should().BeFalse();
    }

    [Fact]
    public void ThePatchedSchemaIsStillAssignableToATool()
    {
        // The library validates InputSchema on assignment. The filter writes the patched document
        // back through that same setter, so a patch that produced something invalid would throw
        // inside every tools/list response — a total outage caused by a cosmetic schema tweak.
        var tool = MakeTool("manage_task", ManageTaskSchema);

        var act = () => PlatformToolSchema.ApplyManagedTaskEnums(new ListToolsResult { Tools = [tool] });

        act.Should().NotThrow();
        tool.InputSchema.GetProperty("properties").GetProperty("action")
            .GetProperty("enum").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public void ApplyManagedTaskEnums_ToleratesNullResultsAndEmptyCollections()
    {
        // The request-filter delegate can hand back null when no tool collection was resolved, and
        // a server with no tools is a legal state during startup.
        var act = () =>
        {
            PlatformToolSchema.ApplyManagedTaskEnums(null);
            PlatformToolSchema.ApplyManagedTaskEnums(new ListToolsResult());
            PlatformToolSchema.ApplyManagedTaskEnums(new ListToolsResult { Tools = [] });
            PlatformToolSchema.ApplyManagedTaskEnums(new ListToolsResult { Tools = [null!] });
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void AllActions_MatchesTheVocabularyTheHandlerAccepts()
    {
        // The advertised enum and the handler's own switch read these constants. If a value were
        // added to one and not the other, a client would be told a call is valid and then refused
        // for making it — the worst kind of contract break, because it looks like a server bug.
        PlatformToolSchema.AllActions.Should().Equal("get_details", "set_status", "list_all");

        PlatformToolSchema.ActionGetDetails.Should().Be("get_details");
        PlatformToolSchema.ActionSetStatus.Should().Be("set_status");
        PlatformToolSchema.ActionListAll.Should().Be("list_all");
    }

    [Fact]
    public void StatusEnumMatchesTheTaskLifecycleVocabulary() =>
        // Same drift argument as above, for the status half: JobTaskStatus.All is what
        // ManageTask validates against and what the error message prints.
        JobTaskStatus.All.Should().Equal("todo", "in_progress", "awaiting_review", "completed");
}
