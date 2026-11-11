using System.Text.Json;
using AgentMesh.Api.Tools;
using AgentMesh.Core.Models;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Tools;

public class PlatformToolCallNormalizerTests
{
    [Fact]
    public void NormalizeArguments_NullOrEmptyArgs_ReturnsSafely()
    {
        var result = PlatformToolCallNormalizer.NormalizeArguments("open_pull_request", null);
        result.Should().NotBeNull();
        result!.ContainsKey("summary_3r").Should().BeTrue();

        var emptyTool = PlatformToolCallNormalizer.NormalizeArguments("inspect_job_status", null);
        emptyTool.Should().NotBeNull();
        emptyTool!.Should().BeEmpty();
    }

    [Fact]
    public void NormalizeArguments_NullOrEmptyToolName_ReturnsRaw()
    {
        var raw = new Dictionary<string, JsonElement>
        {
            ["foo"] = JsonSerializer.SerializeToElement("bar")
        };
        var result = PlatformToolCallNormalizer.NormalizeArguments(null, raw);
        result.Should().BeSameAs(raw);
    }

    [Fact]
    public void NormalizeArguments_OpenPullRequest_WithBodyString_SynthesizesSummary3R()
    {
        var raw = new Dictionary<string, JsonElement>
        {
            ["title"] = JsonSerializer.SerializeToElement("feat(api): test"),
            ["body"] = JsonSerializer.SerializeToElement("Implemented changes as requested."),
            ["target_branch"] = JsonSerializer.SerializeToElement("feat/my-branch"),
            ["jobId"] = JsonSerializer.SerializeToElement("job-123")
        };

        var normalized = PlatformToolCallNormalizer.NormalizeArguments("open_pull_request", raw);
        normalized.Should().NotBeNull();
        normalized!.ContainsKey("summary_3r").Should().BeTrue();
        normalized.ContainsKey("branch_name").Should().BeTrue();
        normalized.ContainsKey("job_id").Should().BeTrue();

        normalized["branch_name"].GetString().Should().Be("feat/my-branch");
        normalized["job_id"].GetString().Should().Be("job-123");

        var summary = normalized["summary_3r"];
        summary.ValueKind.Should().Be(JsonValueKind.Object);
        summary.GetProperty("result").GetString().Should().Be("Implemented changes as requested.");
        summary.GetProperty("reasoning").GetString().Should().NotBeNullOrWhiteSpace();
        summary.GetProperty("risk").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void NormalizeArguments_OpenPullRequest_With3RFormattedText_ParsesEachSection()
    {
        var text = """
            Result: Added file changes endpoint and Cockpit UI.
            Reasoning: Cockpit needed a dedicated Changes tab to review diffs.
            Risk: Minimal regression risk; new routes only.
            """;

        var raw = new Dictionary<string, JsonElement>
        {
            ["title"] = JsonSerializer.SerializeToElement("feat(cockpit): changes tab"),
            ["body"] = JsonSerializer.SerializeToElement(text),
        };

        var normalized = PlatformToolCallNormalizer.NormalizeArguments("open_pull_request", raw);
        normalized.Should().NotBeNull();
        var summary = normalized!["summary_3r"];

        summary.GetProperty("result").GetString().Should().Be("Added file changes endpoint and Cockpit UI.");
        summary.GetProperty("reasoning").GetString().Should().Be("Cockpit needed a dedicated Changes tab to review diffs.");
        summary.GetProperty("risk").GetString().Should().Be("Minimal regression risk; new routes only.");
    }

    [Fact]
    public void NormalizeArguments_OpenPullRequest_WithObjectSummary_PreservesOrFillsDefaults()
    {
        var raw = new Dictionary<string, JsonElement>
        {
            ["title"] = JsonSerializer.SerializeToElement("feat: test"),
            ["summary_3r"] = JsonSerializer.SerializeToElement(new
            {
                result = "Working code",
                reasoning = "Per instructions"
                // risk omitted
            })
        };

        var normalized = PlatformToolCallNormalizer.NormalizeArguments("open_pull_request", raw);
        var summary = normalized!["summary_3r"];

        summary.GetProperty("result").GetString().Should().Be("Working code");
        summary.GetProperty("reasoning").GetString().Should().Be("Per instructions");
        summary.GetProperty("risk").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void NormalizeArguments_SaveMemory_MapsContentToValue_AndAliasesCategories()
    {
        var raw = new Dictionary<string, JsonElement>
        {
            ["key"] = JsonSerializer.SerializeToElement("my_key"),
            ["content"] = JsonSerializer.SerializeToElement("my value content"),
            ["category"] = JsonSerializer.SerializeToElement("architecture"),
            ["isGlobal"] = JsonSerializer.SerializeToElement(true)
        };

        var normalized = PlatformToolCallNormalizer.NormalizeArguments("save_memory", raw);
        normalized.Should().NotBeNull();
        normalized!["value"].GetString().Should().Be("my value content");
        normalized["category"].GetString().Should().Be(AgentMemoryCategories.CodebaseGotcha);
        normalized["is_global"].GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("environment_quirk", AgentMemoryCategories.Environment)]
    [InlineData("test_strategy", AgentMemoryCategories.BuildAndTest)]
    [InlineData("general", AgentMemoryCategories.CodebaseGotcha)]
    [InlineData("codebase_gotcha", AgentMemoryCategories.CodebaseGotcha)]
    public void NormalizeArguments_SaveMemory_NormalizesCategoryCategories(string inputCategory, string expectedCanonical)
    {
        var raw = new Dictionary<string, JsonElement>
        {
            ["key"] = JsonSerializer.SerializeToElement("k"),
            ["value"] = JsonSerializer.SerializeToElement("v"),
            ["category"] = JsonSerializer.SerializeToElement(inputCategory)
        };

        var normalized = PlatformToolCallNormalizer.NormalizeArguments("save_memory", raw);
        normalized!["category"].GetString().Should().Be(expectedCanonical);
    }

    [Fact]
    public void NormalizeArguments_ManageTask_MapsAliasesAndParsesTasksJson()
    {
        var tasksJsonString = """
            [
                {
                    "title": "Task 1",
                    "targetFile": "src/Program.cs",
                    "verificationMethod": "dotnet test"
                }
            ]
            """;

        var raw = new Dictionary<string, JsonElement>
        {
            ["action"] = JsonSerializer.SerializeToElement("set_status"),
            ["taskId"] = JsonSerializer.SerializeToElement("task-123"),
            ["progressNote"] = JsonSerializer.SerializeToElement("in progress"),
            ["jobId"] = JsonSerializer.SerializeToElement("job-456"),
            ["tasks"] = JsonSerializer.SerializeToElement(tasksJsonString)
        };

        var normalized = PlatformToolCallNormalizer.NormalizeArguments("manage_task", raw);
        normalized.Should().NotBeNull();
        normalized!["task_id"].GetString().Should().Be("task-123");
        normalized["progress_note"].GetString().Should().Be("in progress");
        normalized["job_id"].GetString().Should().Be("job-456");

        var tasks = normalized["tasks"];
        tasks.ValueKind.Should().Be(JsonValueKind.Array);
        var firstTask = tasks.EnumerateArray().First();
        firstTask.GetProperty("target_file").GetString().Should().Be("src/Program.cs");
        firstTask.GetProperty("verification_method").GetString().Should().Be("dotnet test");
    }

    [Fact]
    public void NormalizeArguments_PostJobMessage_MapsAliases()
    {
        var raw = new Dictionary<string, JsonElement>
        {
            ["message"] = JsonSerializer.SerializeToElement("Hello lead"),
            ["targetPersona"] = JsonSerializer.SerializeToElement("lead"),
            ["requiresReply"] = JsonSerializer.SerializeToElement("true"),
            ["jobId"] = JsonSerializer.SerializeToElement("job-999")
        };

        var normalized = PlatformToolCallNormalizer.NormalizeArguments("post_job_message", raw);
        normalized.Should().NotBeNull();
        normalized!["content"].GetString().Should().Be("Hello lead");
        normalized["target_persona"].GetString().Should().Be("lead");
        normalized["requires_reply"].GetBoolean().Should().BeTrue();
        normalized["job_id"].GetString().Should().Be("job-999");
    }

    [Fact]
    public void NormalizeArguments_StageAgentIntent_MapsAliases()
    {
        var raw = new Dictionary<string, JsonElement>
        {
            ["target_service"] = JsonSerializer.SerializeToElement("worker"),
            ["action_verb"] = JsonSerializer.SerializeToElement("restart"),
            ["payload_json"] = JsonSerializer.SerializeToElement("{}"),
            ["session_id"] = JsonSerializer.SerializeToElement("sess-1")
        };

        var normalized = PlatformToolCallNormalizer.NormalizeArguments("stage_agent_intent", raw);
        normalized.Should().NotBeNull();
        normalized!["targetService"].GetString().Should().Be("worker");
        normalized["actionVerb"].GetString().Should().Be("restart");
        normalized["payloadJson"].GetString().Should().Be("{}");
        normalized["sessionId"].GetString().Should().Be("sess-1");
    }
}
