using System.Text.Json;
using AgentMesh.Api.Tools;
using AgentMesh.Core.Models;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AgentMesh.Tests.Tools;

/// <summary>
/// The <c>tools/list</c> filter that advertises <c>save_memory</c>'s category constraint.
/// </summary>
/// <remarks>
/// <c>MemoryTools</c> deliberately takes <c>category</c> as a string so a bad value gets an actionable
/// error rather than the library's generic invocation failure. That trades away the generated schema's
/// <c>enum</c>, and this filter is what puts it back. If the filter silently stops matching, the tool
/// still works but every client loses the constraint — a degradation no behavioural test would notice.
/// </remarks>
public class MemoryToolSchemaTests
{
    private static Tool MakeTool(string name, string schemaJson) => new()
    {
        Name = name,
        InputSchema = JsonDocument.Parse(schemaJson).RootElement.Clone(),
    };

    private const string GeneratedSchema = """
        {
          "type": "object",
          "properties": {
            "category": { "description": "The functional classification of the memory.", "type": "string" },
            "key": { "description": "Short, unique identifier.", "type": "string" },
            "value": { "description": "The concrete guideline.", "type": "string" },
            "is_global": { "description": "True if this applies across all projects.", "type": ["boolean", "null"], "default": null }
          },
          "required": ["category", "key", "value"]
        }
        """;

    [Fact]
    public void ApplyCategoryEnum_AddsTheEnumAndKeepsEverythingElse()
    {
        var tool = MakeTool("save_memory", GeneratedSchema);

        MemoryToolSchema.ApplyCategoryEnum(new ListToolsResult { Tools = [tool] });

        using var schema = JsonDocument.Parse(tool.InputSchema.GetRawText());
        var category = schema.RootElement.GetProperty("properties").GetProperty("category");

        category.GetProperty("enum").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(AgentMemoryCategories.All);

        // The rest of the property must survive: dropping the description would leave a model choosing
        // between three bare tokens with no idea what any of them mean.
        category.GetProperty("type").GetString().Should().Be("string");
        category.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();

        schema.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("category", "key", "value", "is_global");
        schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo("category", "key", "value");
    }

    [Fact]
    public void ApplyCategoryEnum_LeavesOtherToolsAlone()
    {
        var saveMemory = MakeTool("save_memory", GeneratedSchema);
        var other = MakeTool("create_session", GeneratedSchema);

        MemoryToolSchema.ApplyCategoryEnum(new ListToolsResult { Tools = [saveMemory, other] });

        other.InputSchema.ToString().Should().Be(JsonDocument.Parse(GeneratedSchema).RootElement.ToString(),
            "this filter exists for save_memory and must not annotate someone else's category argument");
    }

    [Fact]
    public void ApplyCategoryEnum_ReflectsTheCategoryListTheToolValidatesAgainst()
    {
        // The whole point of deriving the enum from AgentMemoryCategories.All: the advertised contract and
        // the enforced one cannot drift. Add a bucket and both move together.
        var tool = MakeTool("save_memory", GeneratedSchema);

        MemoryToolSchema.ApplyCategoryEnum(new ListToolsResult { Tools = [tool] });

        using var schema = JsonDocument.Parse(tool.InputSchema.GetRawText());
        var advertised = schema.RootElement.GetProperty("properties").GetProperty("category")
            .GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();

        advertised.Should().Equal(AgentMemoryCategories.All);
        advertised.Should().OnlyContain(c => AgentMemoryCategories.IsAllowed(c));
    }

    [Fact]
    public void ApplyCategoryEnum_SurvivesAToolWithoutTheExpectedSchema()
    {
        // tools/list runs this filter for every tool on the server. A schema this filter cannot patch must
        // degrade to "the enum is not advertised", never take the endpoint down.
        //
        // These are the shapes the library will actually let a Tool hold: its InputSchema setter rejects a
        // document without a top-level "type": "object", so the truly malformed cases are unreachable and
        // are not tested here.
        var cases = new[]
        {
            ("""{"type":"object"}""", "no properties at all"),
            ("""{"type":"object","properties":null}""", "null properties"),
            ("""{"type":"object","properties":{}}""", "empty properties"),
            ("""{"type":"object","properties":{"key":{"type":"string"}}}""", "no category property"),
            ("""{"type":"object","properties":{"category":"not-an-object"}}""", "category is not an object"),
            ("""{"type":"object","properties":{"category":true}}""", "category is a boolean"),
        };

        foreach (var (schemaJson, reason) in cases)
        {
            var tool = MakeTool("save_memory", schemaJson);

            var act = () => MemoryToolSchema.ApplyCategoryEnum(new ListToolsResult { Tools = [tool] });

            act.Should().NotThrow(reason);
        }
    }

    [Fact]
    public void ApplyCategoryEnum_PatchesACategoryPropertyThatCarriesNoMetadata()
    {
        // The empty-object case is the one that still gets patched: the constraint is worth advertising
        // even when the generator produced nothing else for the property.
        var tool = MakeTool("save_memory", """{"type":"object","properties":{"category":{}}}""");

        MemoryToolSchema.ApplyCategoryEnum(new ListToolsResult { Tools = [tool] });

        using var schema = JsonDocument.Parse(tool.InputSchema.GetRawText());
        schema.RootElement.GetProperty("properties").GetProperty("category").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).Should().Equal(AgentMemoryCategories.All);
    }

    [Fact]
    public void ApplyCategoryEnum_ToleratesNullResultsAndEmptyCollections()
    {
        // The request-filter delegate can hand back null when no tool collection was resolved.
        var act = () =>
        {
            MemoryToolSchema.ApplyCategoryEnum(null);
            MemoryToolSchema.ApplyCategoryEnum(new ListToolsResult());
            MemoryToolSchema.ApplyCategoryEnum(new ListToolsResult { Tools = [] });
            MemoryToolSchema.ApplyCategoryEnum(new ListToolsResult { Tools = [null!] });
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void ApplyCategoryEnum_IsIdempotent()
    {
        // tools/list may be served repeatedly, and a filter that appended rather than replaced would grow
        // the enum on every pass.
        var tool = MakeTool("save_memory", GeneratedSchema);

        MemoryToolSchema.ApplyCategoryEnum(new ListToolsResult { Tools = [tool] });
        var first = tool.InputSchema.GetRawText();

        MemoryToolSchema.ApplyCategoryEnum(new ListToolsResult { Tools = [tool] });

        tool.InputSchema.GetRawText().Should().Be(first);
    }

    [Fact]
    public void SaveMemoryToolName_MatchesTheRegisteredTool()
    {
        // The filter matches on this string. If the tool attribute is renamed and this constant is not, the
        // enum quietly disappears from the wire contract, so pin the two together.
        var attribute = typeof(MemoryTools)
            .GetMethod(nameof(MemoryTools.SaveMemory))!
            .GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false)
            .Cast<ModelContextProtocol.Server.McpServerToolAttribute>()
            .Single();

        attribute.Name.Should().Be(MemoryToolSchema.SaveMemoryToolName);
    }

    [Fact]
    public void SaveMemorySignature_StillCarriesTheCategoryPropertyTheFilterPatches()
    {
        // The other half of that coupling: if the parameter is renamed, the filter finds nothing to patch
        // and the schema loses its constraint with no test otherwise noticing.
        var parameters = typeof(MemoryTools).GetMethod(nameof(MemoryTools.SaveMemory))!
            .GetParameters()
            .Select(p => p.Name)
            .ToList();

        parameters.Should().Contain("category");
        parameters.Should().Contain("key");
        parameters.Should().Contain("value");
        parameters.Should().Contain("is_global");
    }
}
