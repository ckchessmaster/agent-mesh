using AgentMesh.Api.Services;
using FluentAssertions;
using System.Text.Json;
using Xunit;

namespace AgentMesh.Tests.Services;

/// <summary>
/// Capability blobs have two authors: the CLI (pushes its whole local preset) and the dashboard
/// (only knows the fields it renders). These tests pin the lossless-merge contract that keeps
/// either side from silently erasing what the other wrote.
/// </summary>
public class DriverCapabilitiesTests
{
    [Fact]
    public void Merge_ObjectWithObject_PreservesKeysTheIncomingPatchOmits()
    {
        var existing = """
            {"adapter":"openai","endpoint":"http://127.0.0.1:11435/v1","apiKey":"secret",
             "maxSteps":100,"timeoutSeconds":300,"maxRetries":4,"retryDelayMilliseconds":250,
             "capabilities":["run_command","ask_question"],"models":[{"id":"llama3.3","name":"Llama 3.3"}]}
            """;

        // Exactly what the dashboard used to send: a rebuilt blob with only its own four fields.
        var incoming = """
            {"adapter":"openai","endpoint":"http://127.0.0.1:11435/v1","apiKey":"secret",
             "defaultModel":"llama3.3","models":[{"id":"llama3.3","name":"Llama 3.3"}]}
            """;

        var merged = DriverCapabilities.Merge(existing, incoming);
        using var doc = JsonDocument.Parse(merged!);
        var root = doc.RootElement;

        root.GetProperty("maxSteps").GetInt32().Should().Be(100);
        root.GetProperty("timeoutSeconds").GetInt32().Should().Be(300);
        root.GetProperty("maxRetries").GetInt32().Should().Be(4);
        root.GetProperty("retryDelayMilliseconds").GetInt32().Should().Be(250);
        root.GetProperty("capabilities").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void Merge_OverwritesOnlyKeysThePatchActuallySets()
    {
        var existing = """{"adapter":"openai","maxSteps":100,"endpoint":"http://old/v1"}""";
        var incoming = """{"maxSteps":25}""";

        var merged = DriverCapabilities.Merge(existing, incoming);
        using var doc = JsonDocument.Parse(merged!);

        doc.RootElement.GetProperty("maxSteps").GetInt32().Should().Be(25);
        doc.RootElement.GetProperty("endpoint").GetString().Should().Be("http://old/v1");
        doc.RootElement.GetProperty("adapter").GetString().Should().Be("openai");
    }

    [Fact]
    public void Merge_SkipsNullValuesSoAbsentMeansDontTouch()
    {
        // The CLI serializes unset optionals as JSON null. That must read as "not set",
        // not as "clear the operator's value".
        var existing = """{"adapter":"openai","maxSteps":42}""";
        var incoming = """{"adapter":"openai","maxSteps":null,"timeoutSeconds":null}""";

        var merged = DriverCapabilities.Merge(existing, incoming);
        using var doc = JsonDocument.Parse(merged!);

        doc.RootElement.GetProperty("maxSteps").GetInt32().Should().Be(42);
        doc.RootElement.TryGetProperty("timeoutSeconds", out _).Should().BeFalse();
    }

    [Fact]
    public void Merge_ArrayExistingNormalizedToObject_KeepsCapabilitiesWhenLimitsAdded()
    {
        var existing = """["bash","read_file","grep"]""";
        var incoming = """{"maxSteps":60}""";

        var merged = DriverCapabilities.Merge(existing, incoming);
        using var doc = JsonDocument.Parse(merged!);

        doc.RootElement.GetProperty("maxSteps").GetInt32().Should().Be(60);
        var caps = doc.RootElement.GetProperty("capabilities").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        caps.Should().Equal("bash", "read_file", "grep");
    }

    [Fact]
    public void Merge_BothArrays_ReplacesCapabilityList()
    {
        var existing = """["bash","read_file"]""";
        var incoming = """["run_command"]""";

        DriverCapabilities.Merge(existing, incoming).Should().Be("""["run_command"]""");
    }

    [Fact]
    public void Merge_ObjectExistingWithArrayIncoming_KeepsLimitsAndSwapsCapabilities()
    {
        var existing = """{"adapter":"openai","maxSteps":77,"capabilities":["a"]}""";
        var incoming = """["b","c"]""";

        var merged = DriverCapabilities.Merge(existing, incoming);
        using var doc = JsonDocument.Parse(merged!);

        doc.RootElement.GetProperty("maxSteps").GetInt32().Should().Be(77);
        doc.RootElement.GetProperty("capabilities").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void Merge_EmptyIncoming_LeavesExistingUntouched()
    {
        DriverCapabilities.Merge("""{"maxSteps":9}""", null).Should().Be("""{"maxSteps":9}""");
        DriverCapabilities.Merge("""{"maxSteps":9}""", "  ").Should().Be("""{"maxSteps":9}""");
    }

    [Fact]
    public void Merge_UnparseableIncoming_DoesNotDestroyStoredConfig()
    {
        // A malformed write must never blank out a working driver's config.
        DriverCapabilities.Merge("""{"maxSteps":9}""", "{not json").Should().Be("""{"maxSteps":9}""");
    }

    [Fact]
    public void Merge_UnparseableExisting_FallsBackToIncoming()
    {
        var merged = DriverCapabilities.Merge("}{garbage", """{"maxSteps":9}""");
        using var doc = JsonDocument.Parse(merged!);
        doc.RootElement.GetProperty("maxSteps").GetInt32().Should().Be(9);
    }

    [Fact]
    public void Merge_ResultIsValidJson_AndDoesNotAliasNodesBetweenDocuments()
    {
        // JsonNode throws if a node with a parent is re-parented; DeepClone guards against it.
        var existing = """{"a":{"nested":1},"maxSteps":1}""";
        var incoming = """{"a":{"nested":2},"b":{"nested":3}}""";

        var act = () => DriverCapabilities.Merge(existing, incoming);

        act.Should().NotThrow();
        JsonDocument.Parse(DriverCapabilities.Merge(existing, incoming)!).RootElement
            .GetProperty("a").GetProperty("nested").GetInt32().Should().Be(2);
    }

    [Fact]
    public void Parse_ExposesLimitsForUiWithoutLeakingApiKey()
    {
        var json = """
            {"adapter":"openai","endpoint":"http://h:11435/v1","apiKey":"super-secret",
             "defaultModel":"qwen3","maxSteps":100,"timeoutSeconds":300,"maxRetries":4,
             "retryDelayMilliseconds":250,"capabilities":["run_command"]}
            """;

        var cfg = DriverCapabilities.Parse(json);

        cfg.Adapter.Should().Be("openai");
        cfg.Endpoint.Should().Be("http://h:11435/v1");
        cfg.DefaultModel.Should().Be("qwen3");
        cfg.MaxSteps.Should().Be(100);
        cfg.TimeoutSeconds.Should().Be(300);
        cfg.MaxRetries.Should().Be(4);
        cfg.RetryDelayMilliseconds.Should().Be(250);
        cfg.Capabilities.Should().Equal("run_command");
        cfg.HasApiKey.Should().BeTrue();
    }

    [Fact]
    public void Parse_ArrayShapedCaps_StillSurfacesCapabilities()
    {
        var cfg = DriverCapabilities.Parse("""["bash","grep"]""");

        cfg.Adapter.Should().BeNull();
        cfg.Capabilities.Should().Equal("bash", "grep");
        cfg.MaxSteps.Should().BeNull();
    }

    [Fact]
    public void Parse_EmptyOrNull_IsSafe()
    {
        DriverCapabilities.Parse(null).Capabilities.Should().BeEmpty();
        DriverCapabilities.Parse("[]").Capabilities.Should().BeEmpty();
        DriverCapabilities.Parse("nonsense").Capabilities.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ReportsDefaultModelFromTopLevelKey()
    {
        // The dashboard writes `defaultModel`; older CLI blobs may omit it entirely.
        DriverCapabilities.Parse("""{"defaultModel":"m1"}""").DefaultModel.Should().Be("m1");
        DriverCapabilities.Parse("""{"adapter":"openai"}""").DefaultModel.Should().BeNull();
    }
}
