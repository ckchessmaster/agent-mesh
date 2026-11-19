using System.Text.Json;
using AgentMesh.Cli.Commands;
using AgentMesh.Cli.Models;
using AgentMesh.Cli.Services;
using FluentAssertions;

namespace AgentMesh.Tests.Cli;

/// <summary>
/// The shell route to the control plane for drivers that get no function tools.
/// </summary>
/// <remarks>
/// <para>
/// A Stage 1B decomposition on the agy driver called Antigravity's own built-in <c>manage_task</c> — a
/// background-shell manager — and reported success. The checklist never reached the platform, the plan gate
/// was built from the worker's invented fallback, and nothing anywhere said a word about it. These verbs are
/// the fix: a command called <c>agentmesh task</c> cannot be shadowed by a harness built-in, and a call that
/// cannot be placed on a job now fails loudly instead of quietly.
/// </para>
/// <para>
/// What is pinned here is the parts that made the incident invisible: the tool the arguments actually go to,
/// that a checklist with nothing in it is never sent, and that a missing turn context explains itself.
/// </para>
/// </remarks>
public class PlatformToolCommandTests
{
    private static CliConfig NewConfig() => new() { MeshUrl = "http://localhost:5000", MasterToken = "test-token" };

    private sealed record Call(string Tool, string Args, Guid? SessionId, string? PersonaId, string? AgentId);

    private static Func<PlatformToolCommand.McpToolExecutor> Succeeding(
        List<Call> calls,
        string response = "{\"success\":true}") =>
        () => (tool, argsJson, sid, pid, aid, _) =>
        {
            calls.Add(new Call(tool, argsJson, sid, pid, aid));
            return Task.FromResult(response);
        };

    private static JsonElement Arg(List<Call> calls, string name)
    {
        using var doc = JsonDocument.Parse(calls.Single().Args);
        return doc.RootElement.Clone().GetProperty(name);
    }

    /// <summary>A field the command must not make up: absent, or present and null.</summary>
    private static void HasNoValue(JsonElement task, string name)
    {
        var present = task.TryGetProperty(name, out var value);
        (present && value.ValueKind != JsonValueKind.Null).Should().BeFalse($"{name} was invented out of nothing");
    }

    [Fact]
    public async Task SetChecklist_RegistersAgainstManageTaskNotAHarnessBuiltin()
    {
        var calls = new List<Call>();
        var sessionId = Guid.NewGuid();

        var code = await PlatformToolCommand.ExecuteTaskAsync(
            [
                "set-checklist",
                "--json", "[{\"title\":\"Add reallocation endpoint\",\"target_file\":\"internal/server/routes.go\",\"verification_method\":\"go test ./...\"}]",
                "--session-id", sessionId.ToString(),
                "--job-id", "job-42",
                "--persona", "astra",
                "--agent", "agy-default"
            ],
            NewConfig(),
            new ConfigStore(),
            new StringWriter(),
            new StringWriter(),
            mcpExecutor: Succeeding(calls)());

        code.Should().Be(PlatformToolCommand.SuccessExitCode);
        calls.Should().ContainSingle();
        calls[0].Tool.Should().Be("manage_task");
        calls[0].SessionId.Should().Be(sessionId);
        calls[0].PersonaId.Should().Be("astra");
        calls[0].AgentId.Should().Be("agy-default");
        Arg(calls, "action").GetString().Should().Be("set_checklist");
        Arg(calls, "job_id").GetString().Should().Be("job-42");

        var task = Arg(calls, "tasks")[0];
        task.GetProperty("title").GetString().Should().Be("Add reallocation endpoint");
        task.GetProperty("target_file").GetString().Should().Be("internal/server/routes.go");
        task.GetProperty("verification_method").GetString().Should().Be("go test ./...");
    }

    [Fact]
    public async Task SetChecklist_AcceptsCamelCaseKeysAndAWrappedTasksObject()
    {
        var calls = new List<Call>();

        var code = await PlatformToolCommand.ExecuteTaskAsync(
            [
                "set-checklist",
                "--json", "{\"tasks\":[{\"name\":\"Wire the queue\",\"targetFile\":\"jobs/publish.ts\"},{\"task\":\"Ship the reader\"}]}"
            ],
            NewConfig(),
            new ConfigStore(),
            new StringWriter(),
            new StringWriter(),
            mcpExecutor: Succeeding(calls)());

        code.Should().Be(PlatformToolCommand.SuccessExitCode, "an agent writes JSON from memory, not from a schema");
        var tasks = Arg(calls, "tasks");
        tasks.GetArrayLength().Should().Be(2);
        tasks[0].GetProperty("title").GetString().Should().Be("Wire the queue");
        tasks[0].GetProperty("target_file").GetString().Should().Be("jobs/publish.ts");
        tasks[1].GetProperty("title").GetString().Should().Be("Ship the reader");
        HasNoValue(tasks[1], "target_file");
    }

    [Fact]
    public async Task SetChecklist_WhenNothingIsReadable_ReportsNoTasksAndSendsNothing()
    {
        var calls = new List<Call>();
        var stderr = new StringWriter();

        var code = await PlatformToolCommand.ExecuteTaskAsync(
            ["set-checklist", "--json", "[{\"path\":\"only-a-file.md\"}]"],
            NewConfig(),
            new ConfigStore(),
            new StringWriter(),
            stderr,
            mcpExecutor: Succeeding(calls)());

        code.Should().Be(PlatformToolCommand.ErrorExitCode);
        calls.Should().BeEmpty("an empty checklist must not overwrite the one the operator already approved");
        stderr.ToString().Should().Contain("NO_TASKS").And.Contain("Nothing was registered");
    }

    [Fact]
    public async Task SetChecklist_WhenNoInputIsGiven_NamesThePlanGateAsTheReason()
    {
        var calls = new List<Call>();
        var stderr = new StringWriter();

        var code = await PlatformToolCommand.ExecuteTaskAsync(
            ["set-checklist", "--job-id", "job-42"],
            NewConfig(),
            new ConfigStore(),
            new StringWriter(),
            stderr,
            mcpExecutor: Succeeding(calls)());

        code.Should().Be(PlatformToolCommand.ErrorExitCode);
        calls.Should().BeEmpty();
        stderr.ToString().Should().Contain("NO_INPUT").And.Contain("plan gate");
    }

    [Fact]
    public async Task UpdateStatus_WhenTaskIdMissing_ExplainsTheFlagsAndPointsAtGetChecklist()
    {
        var calls = new List<Call>();
        var stderr = new StringWriter();

        var code = await PlatformToolCommand.ExecuteTaskAsync(
            ["update-status", "--status", "in_progress"],
            NewConfig(),
            new ConfigStore(),
            new StringWriter(),
            stderr,
            mcpExecutor: Succeeding(calls)());

        code.Should().Be(PlatformToolCommand.ErrorExitCode);
        calls.Should().BeEmpty();
        stderr.ToString().Should().Contain("MISSING_ARGUMENT")
            .And.Contain("--task")
            .And.Contain("agentmesh task get-checklist");
    }

    [Fact]
    public async Task GetChecklist_AsksWithoutInventingArgumentsTheToolDoesNotTake()
    {
        var calls = new List<Call>();

        var code = await PlatformToolCommand.ExecuteTaskAsync(
            ["get-checklist"],
            NewConfig(),
            new ConfigStore(),
            new StringWriter(),
            new StringWriter(),
            mcpExecutor: Succeeding(calls)());

        code.Should().Be(PlatformToolCommand.SuccessExitCode);
        Arg(calls, "action").GetString().Should().Be("get_checklist");
    }

    [Fact]
    public async Task MessagePost_RegistersAgainstPostJobMessageAndDefaultsToTheOperator()
    {
        var calls = new List<Call>();

        var code = await PlatformToolCommand.ExecuteMessageAsync(
            ["post", "--content", "Blocked on the staging credentials.", "--requires-reply"],
            NewConfig(),
            new ConfigStore(),
            new StringWriter(),
            new StringWriter(),
            mcpExecutor: Succeeding(calls)());

        code.Should().Be(PlatformToolCommand.SuccessExitCode);
        calls.Single().Tool.Should().Be("post_job_message");
        Arg(calls, "content").GetString().Should().Be("Blocked on the staging credentials.");
        Arg(calls, "target_persona").GetString().Should().Be("operator");
        Arg(calls, "requires_reply").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task MessagePost_WhenBodyIsMissing_FailsWithoutPosting()
    {
        var calls = new List<Call>();
        var stderr = new StringWriter();

        var code = await PlatformToolCommand.ExecuteMessageAsync(
            ["post", "--target", "rook"],
            NewConfig(),
            new ConfigStore(),
            new StringWriter(),
            stderr,
            mcpExecutor: Succeeding(calls)());

        code.Should().Be(PlatformToolCommand.ErrorExitCode);
        calls.Should().BeEmpty();
        stderr.ToString().Should().Contain("NO_CONTENT");
    }

    [Fact]
    public async Task WhenShellHasNoTurnContext_HintNamesTheEnvironmentVars()
    {
        var calls = new List<Call>();
        var stderr = new StringWriter();

        var code = await PlatformToolCommand.ExecuteTaskAsync(
            ["set-checklist", "--json", "[{\"title\":\"Real task\"}]"],
            NewConfig(),
            new ConfigStore(),
            new StringWriter(),
            stderr,
            mcpExecutor: Succeeding(calls, response: "Error: no job resolved for this call.")());

        code.Should().Be(PlatformToolCommand.ErrorExitCode);
        calls.Should().ContainSingle("the control plane, not the CLI, is what rejected the call");

        // The generic "call failed" is what sent the agent hunting through its own transcript for a tool
        // that was never the problem, so the hint has to name what is actually missing.
        var hint = stderr.ToString();
        hint.Should().Contain(PlatformToolCommand.SessionIdEnvVar)
            .And.Contain(PlatformToolCommand.JobIdEnvVar)
            .And.Contain("no turn context");
    }

    [Fact]
    public async Task WhenContextIsPresent_ButToolRejects_HintReportsItWithoutBlamingTheEnvironment()
    {
        var calls = new List<Call>();
        var stderr = new StringWriter();
        var sessionId = Guid.NewGuid();

        var code = await PlatformToolCommand.ExecuteTaskAsync(
            ["set-checklist", "--json", "[{\"title\":\"Real task\"}]", "--session-id", sessionId.ToString()],
            NewConfig(),
            new ConfigStore(),
            new StringWriter(),
            stderr,
            mcpExecutor: Succeeding(calls, response: "{\"success\":false,\"error\":\"task titles must be unique\"}")());

        code.Should().Be(PlatformToolCommand.ErrorExitCode);
        var hint = stderr.ToString();
        hint.Should().Contain($"session={sessionId}")
            .And.NotContain("no turn context", "the context was there; blaming it would send the agent down a dead end");
    }

    [Fact]
    public async Task Help_PrintsVerbUsageInsteadOfLaunchingTheRunner()
    {
        var stdout = new StringWriter();

        var code = await PlatformToolCommand.ExecuteTaskAsync(
            ["--help"],
            NewConfig(),
            new ConfigStore(),
            stdout,
            new StringWriter());

        code.Should().Be(PlatformToolCommand.SuccessExitCode);
        stdout.ToString().Should().Contain("agentmesh task set-checklist")
            .And.Contain(PlatformToolCommand.SessionIdEnvVar);
    }

    [Fact]
    public async Task UnknownVerb_IsRejectedLocallyAndNeverReachesThePlatform()
    {
        var calls = new List<Call>();
        var stderr = new StringWriter();

        var code = await PlatformToolCommand.ExecuteTaskAsync(
            ["destroy-everything"],
            NewConfig(),
            new ConfigStore(),
            new StringWriter(),
            stderr,
            mcpExecutor: Succeeding(calls)());

        code.Should().Be(PlatformToolCommand.ErrorExitCode);
        calls.Should().BeEmpty();
        stderr.ToString().Should().Contain("Unknown `agentmesh task` subcommand");
    }

    [Theory]
    [InlineData("[{\"title\":\"Plain\"}]", "Plain")]
    [InlineData("[\"Plain\"]", "Plain")]
    [InlineData("\"Plain\"", "Plain")]
    [InlineData("{\"title\":\"Plain\"}", "Plain")]
    [InlineData("[{\"Title\":\"Plain\",\"Task\":\"ignored\"}]", "Plain")]
    [InlineData("  [{\"title\" : \"Plain\" }]  ", "Plain")]
    public void ReadChecklistTasks_AcceptsEveryShapeAnAgentWritesFromMemory(string json, string expected)
    {
        PlatformToolCommand.ReadChecklistTasks(json)
            .Select(task => task.Title)
            .Should().ContainSingle()
            .Which.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[{\"path\":\"no-title.md\"}]")]
    [InlineData("{}")]
    [InlineData("[1,2,3]")]
    public void ReadChecklistTasks_NeverInventsATitleFromSomethingWithoutOne(string json)
    {
        PlatformToolCommand.ReadChecklistTasks(json).Should().BeEmpty();
    }

    /// <summary>
    /// The answer shapes that mean the checklist was not registered.
    /// </summary>
    /// <remarks>
    /// A connection refused used to read as success, because the client's transport message is "Error
    /// executing platform tool …" and the check only recognised "Error:". An agent told its call worked keeps
    /// going and concludes the turn believing the plan gate is populated; that is how job c28eca57 arrived at
    /// a plan nobody wrote.
    /// </remarks>
    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("Error executing platform tool 'manage_task': Connection refused (127.0.0.1:59999)", true)]
    [InlineData("Error: MCP call to 'manage_task' failed with HTTP status 401 (Unauthorized)", true)]
    [InlineData("Error: MCP tool 'manage_task' returned error: not bound to a job", true)]
    [InlineData("{\"success\":false,\"error\":\"missing_tasks\",\"message\":\"set_checklist requires a non-empty list of tasks.\"}", true)]
    [InlineData("{\"success\":false,\"count\":0}", true)]
    [InlineData("{\"error\":\"task_count_exceeded\"}", true)]
    [InlineData("{\"success\":true,\"count\":3}", false)]
    [InlineData("{\"success\":true,\"error\":null}", false)]
    [InlineData("{\"tasks\":[{\"title\":\"Real task\"}]}", false)]
    [InlineData("Checklist set with 3 tasks.", false)]
    [InlineData("No checklist tasks found for this job.", false)]
    public void LooksLikeFailure_SeparatesARejectedCallFromAnEmptySuccess(string response, bool expected)
    {
        PlatformToolCommand.LooksLikeFailure(response).Should().Be(expected);
    }

    [Fact]
    public async Task WhenTheControlPlaneIsUnreachable_TheCommandExitsNonZero()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await PlatformToolCommand.ExecuteTaskAsync(
            ["set-checklist", "--json", "[{\"title\":\"Real task\"}]", "--job-id", "job-42"],
            NewConfig(),
            new ConfigStore(),
            stdout,
            stderr,
            mcpExecutor: Succeeding(
                new List<Call>(),
                response: "Error executing platform tool 'manage_task': Connection refused (10.0.0.8:8080)")());

        code.Should().Be(PlatformToolCommand.ErrorExitCode,
            "a driver that reads a zero exit code will conclude its checklist is registered");
        stdout.ToString().Should().BeEmpty("the failure belongs on stderr, not in the transcript as an answer");
        stderr.ToString().Should().Contain("Connection refused");
    }
}
