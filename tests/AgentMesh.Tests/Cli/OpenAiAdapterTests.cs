using System.Diagnostics;
using System.Text.Json;
using AgentMesh.Cli.Adapters;
using AgentMesh.Cli.Models;
using AgentMesh.Cli.Services;
using AgentMesh.Core.Models;
using FluentAssertions;

namespace AgentMesh.Tests.Cli;

/// <remarks>
/// Shares the <c>OpenAiAdapterStaticSeams</c> collection with
/// <see cref="OpenAiAdapterMessageOrderingTests"/>. Both drive the adapter through its static test
/// seams — <c>SetHttpClientForTest</c>, <c>HistoryDirectoryPath</c> — which are process-wide. xUnit
/// parallelises distinct classes by default, so two classes swapping the same static under test would
/// intermittently send each other's requests to each other's stub endpoints.
/// </remarks>
[Collection("OpenAiAdapterStaticSeams")]
public class OpenAiAdapterTests
{
    [Theory]
    [InlineData("openai")]
    [InlineData("openai-compatible")]
    [InlineData("ollama")]
    [InlineData("vllm")]
    public void AgentAdapterFactory_Create_ReturnsOpenAiAdapter_ForSupportedAdapterTypes(string adapterType)
    {
        var preset = new AgentPresetConfig
        {
            Id = "my-llm",
            Name = "Local LLM",
            AdapterType = adapterType
        };

        var adapter = AgentAdapterFactory.Create(preset);
        adapter.Should().BeOfType<OpenAiAdapter>();
        adapter.AdapterType.Should().Be("openai");
    }

    [Theory]
    [InlineData("openai-server", "openai")]
    [InlineData("ollama-local", "openai")]
    [InlineData("vllm-node", "openai")]
    [InlineData("lmstudio-box", "openai")]
    [InlineData("copilot-agent", "copilot")]
    [InlineData("claude-bot", "claude")]
    [InlineData("agy-dev", "agy")]
    public void ConfigStore_InferAdapterType_IdentifiesOpenAiFamily(string id, string expected)
    {
        var inferred = ConfigStore.InferAdapterType(id);
        inferred.Should().Be(expected);
    }

    [Fact]
    public void OpenAiAdapter_GetToolDefinitions_ContainsAllRequiredAutonomousTools()
    {
        var tools = OpenAiAdapter.GetToolDefinitions();
        tools.Should().HaveCount(10);

        var toolNames = new List<string>();
        foreach (var t in tools)
        {
            var json = JsonSerializer.Serialize(t);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("function", out var fn) &&
                fn.TryGetProperty("name", out var n))
            {
                toolNames.Add(n.GetString()!);
            }
        }

        toolNames.Should().Contain("run_command");
        toolNames.Should().Contain("read_file");
        toolNames.Should().Contain("write_file");
        toolNames.Should().Contain("edit_file");
        toolNames.Should().Contain("ask_question");
        toolNames.Should().Contain("manage_task");
        toolNames.Should().Contain("post_job_message");
        toolNames.Should().Contain("open_pull_request");
        toolNames.Should().Contain("save_memory");
        toolNames.Should().Contain("list_memories");
    }

    [Fact]
    public void ResolveEffectiveWorkingDirectory_DefaultsContainerSessionsToProjectRepoUnderWorkspaceRepos()
    {
        var ev = new PromptReceivedEvent
        {
            SessionId = Guid.NewGuid(),
            ProjectId = "payments-api",
            RepositoryName = "payments-api",
            WorkingDirectory = null
        };

        var resolved = AgentProcessManager.ResolveEffectiveWorkingDirectory(
            ev,
            directoryExists: path => path == "/workspace" || path == "/workspace/repos");

        resolved.Should().Be("/workspace/repos/payments-api");

        // Even if a legacy caller passed "/workspace", an active project resolves to /workspace/repos/<repo>
        ev.WorkingDirectory = "/workspace";
        AgentProcessManager.ResolveEffectiveWorkingDirectory(
            ev,
            directoryExists: path => path == "/workspace").Should().Be("/workspace/repos/payments-api");
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_RunCommand_ExecutesAndReturnsOutput()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_tool_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var args = JsonSerializer.Serialize(new { command = "echo 'OpenAiAdapter tool test'" });
            var result = await OpenAiAdapter.ExecuteToolLocallyAsync("run_command", args, tempDir, CancellationToken.None);

            result.Should().Contain("OpenAiAdapter tool test");
            result.Should().Contain("Exit Code: 0");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_AlreadyCancelled_DoesNotCreateRunCommandWorkingDirectory()
    {
        // A cancelled prompt must not leave a workspace behind. `run_command` mkdirs its working
        // directory before the sandbox is even reached, so the old ordering created the directory
        // and then abandoned it, which reads as real output to whatever resumes the session.
        var root = Path.Combine(Path.GetTempPath(), "agentmesh_cancel_test_" + Guid.NewGuid().ToString("N")[..8]);
        var nested = Path.Combine(root, "deep", "deeper");
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var args = JsonSerializer.Serialize(new { command = "echo should-not-run", workingDirectory = nested });
            Func<Task> act = async () => await OpenAiAdapter.ExecuteToolLocallyAsync("run_command", args, root, cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            Directory.Exists(nested).Should().BeFalse("a cancelled prompt must not create working directories");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_AlreadyCancelled_DoesNotWriteFileOrItsParentDirectory()
    {
        // The second mutating door: `write_file` mkdirs the target's parent and then writes it.
        var root = Path.Combine(Path.GetTempPath(), "agentmesh_cancel_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var args = JsonSerializer.Serialize(new { path = "sub/out.txt", content = "must never be written" });
            Func<Task> act = async () => await OpenAiAdapter.ExecuteToolLocallyAsync("write_file", args, root, cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            File.Exists(Path.Combine(root, "sub", "out.txt")).Should().BeFalse("a cancelled prompt must not write files");
            Directory.Exists(Path.Combine(root, "sub")).Should().BeFalse("a cancelled prompt must not create the target's parent directory");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_AlreadyCancelled_UncreatableWorkingDirectory_StillReportsCancellation()
    {
        // The masking case, and the reason checking the token is not merely tidy. A regular file
        // standing where a directory would have to be makes the mkdir throw an IOException, while a
        // cancelled token produces no exception of its own, so the generic catch converted the
        // cancellation into a "Tool execution error: ..." string. The caller then saw a tool that
        // had run and failed when in truth the turn had been cancelled before anything ran.
        var root = Path.Combine(Path.GetTempPath(), "agentmesh_cancel_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var blocker = Path.Combine(root, "blocker");
        File.WriteAllText(blocker, "not a directory");
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var args = JsonSerializer.Serialize(new { command = "echo should-not-run", workingDirectory = Path.Combine(blocker, "deeper") });
            Func<Task> act = async () => await OpenAiAdapter.ExecuteToolLocallyAsync("run_command", args, root, cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_WriteAndReadFile_Succeeds()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_tool_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            // Write
            var writeArgs = JsonSerializer.Serialize(new
            {
                path = "test.txt",
                content = "Line 1\nLine 2\nLine 3"
            });
            var writeRes = await OpenAiAdapter.ExecuteToolLocallyAsync("write_file", writeArgs, tempDir, CancellationToken.None);
            writeRes.Should().Contain("Successfully wrote");

            // Read
            var readArgs = JsonSerializer.Serialize(new
            {
                path = "test.txt",
                startLine = 2,
                endLine = 3
            });
            var readRes = await OpenAiAdapter.ExecuteToolLocallyAsync("read_file", readArgs, tempDir, CancellationToken.None);
            readRes.Should().Contain("2: Line 2");
            readRes.Should().Contain("3: Line 3");
            readRes.Should().NotContain("1: Line 1");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_EditFile_ReplacesTargetAndReportsMissing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_tool_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "config.json");
            await File.WriteAllTextAsync(filePath, "{\"port\": 8080, \"debug\": false}");

            // Successful edit
            var editArgs = JsonSerializer.Serialize(new
            {
                path = "config.json",
                target = "\"debug\": false",
                replacement = "\"debug\": true"
            });
            var editRes = await OpenAiAdapter.ExecuteToolLocallyAsync("edit_file", editArgs, tempDir, CancellationToken.None);
            editRes.Should().Contain("Successfully replaced");

            var updated = await File.ReadAllTextAsync(filePath);
            updated.Should().Contain("\"debug\": true");

            // Missing target error
            var failArgs = JsonSerializer.Serialize(new
            {
                path = "config.json",
                target = "non_existent_key",
                replacement = "new_val"
            });
            var failRes = await OpenAiAdapter.ExecuteToolLocallyAsync("edit_file", failArgs, tempDir, CancellationToken.None);
            failRes.Should().Contain("Error: Target string not found");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_EditFile_RefusesAmbiguousTarget_Unmodified()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_tool_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "dup.txt");
            await File.WriteAllTextAsync(filePath, "alpha beta alpha gamma alpha");

            var ambiguousArgs = JsonSerializer.Serialize(new
            {
                path = "dup.txt",
                target = "alpha",
                replacement = "ALPHA"
            });
            var ambiguousRes = await OpenAiAdapter.ExecuteToolLocallyAsync("edit_file", ambiguousArgs, tempDir, CancellationToken.None);
            ambiguousRes.Should().Contain("appears 3 times");
            ambiguousRes.Should().Contain("replace_all");

            // The refusal must not touch the file — a partial or global clobber on an ambiguous
            // target is the exact silent-corruption failure this guard exists to stop.
            (await File.ReadAllTextAsync(filePath)).Should().Be("alpha beta alpha gamma alpha");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_EditFile_ScopedTargetAnd_ReplaceAll_BothSucceed()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_tool_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "dup.txt");
            await File.WriteAllTextAsync(filePath, "alpha beta alpha gamma alpha");

            // Unique via surrounding context: replaces exactly one occurrence.
            var scopedArgs = JsonSerializer.Serialize(new
            {
                path = "dup.txt",
                target = "alpha beta",
                replacement = "ALPHA beta"
            });
            var scopedRes = await OpenAiAdapter.ExecuteToolLocallyAsync("edit_file", scopedArgs, tempDir, CancellationToken.None);
            scopedRes.Should().Contain("Successfully replaced target");
            (await File.ReadAllTextAsync(filePath)).Should().Be("ALPHA beta alpha gamma alpha");

            // Explicit replace_all is the only path that may touch multiple sites.
            var allArgs = JsonSerializer.Serialize(new
            {
                path = "dup.txt",
                target = "alpha",
                replacement = "ALPHA",
                replace_all = true
            });
            var allRes = await OpenAiAdapter.ExecuteToolLocallyAsync("edit_file", allArgs, tempDir, CancellationToken.None);
            allRes.Should().Contain("replaced all 2 occurrences");
            (await File.ReadAllTextAsync(filePath)).Should().Be("ALPHA beta ALPHA gamma ALPHA");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetToolDefinitions_EditFile_ExposesReplaceAllParameter()
    {
        var tools = OpenAiAdapter.GetToolDefinitions();
        var json = JsonSerializer.Serialize(tools);
        json.Should().Contain("replace_all");
    }

    // ---------- numbered-view paste normalization ----------

    [Fact]
    public async Task ExecuteToolLocallyAsync_EditFile_AcceptsTargetPastedFromNumberedView()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_tool_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "src.txt");
            await File.WriteAllTextAsync(filePath, "alpha one\nbeta two\ngamma three\n");

            // The model pasted straight out of read_file's "N: " display format.
            var editArgs = JsonSerializer.Serialize(new
            {
                path = "src.txt",
                target = "1: alpha one\n2: beta two",
                replacement = "ALPHA one\nBETA two"
            });
            var res = await OpenAiAdapter.ExecuteToolLocallyAsync("edit_file", editArgs, tempDir, CancellationToken.None);
            res.Should().Contain("Successfully replaced target");
            res.Should().Contain("line-number prefixes");
            (await File.ReadAllTextAsync(filePath)).Should().Be("ALPHA one\nBETA two\ngamma three\n");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_EditFile_NeverNormalizesIntoMidLineMatches()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_tool_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "notes.txt");
            await File.WriteAllTextAsync(filePath, "42: meaning of config\nother line\n");

            // Stripping this target's prefix yields "meaning of config", which occurs only
            // MID-line (after "42: "). Line-boundary semantics must refuse rather than rewrite
            // a substring of a legitimately-numbered line.
            var editArgs = JsonSerializer.Serialize(new
            {
                path = "notes.txt",
                target = "1: meaning of config",
                replacement = "HACKED"
            });
            var res = await OpenAiAdapter.ExecuteToolLocallyAsync("edit_file", editArgs, tempDir, CancellationToken.None);
            res.Should().Contain("Error: Target string not found");
            res.Should().Contain("0 matches");
            (await File.ReadAllTextAsync(filePath)).Should().Be("42: meaning of config\nother line\n");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // ---------- fallback tool-call parsing: leak execution paths ----------

    [Fact]
    public void TryParseFallbackToolCall_ParsesQwenXmlFunctionBlocks()
    {
        var content = "Sure. <tool_call>\n<function=run_command>\n<parameter=command>ls -la /tmp</parameter>\n<parameter=timeout_seconds>45</parameter>\n</function>\n</tool_call>";
        var (hasTool, name, args) = OpenAiAdapter.TryParseFallbackToolCall(content);
        hasTool.Should().BeTrue();
        name.Should().Be("run_command");
        using var doc = JsonDocument.Parse(args!);
        doc.RootElement.GetProperty("command").GetString().Should().Be("ls -la /tmp");
        doc.RootElement.GetProperty("timeout_seconds").ValueKind.Should().Be(JsonValueKind.Number);
        doc.RootElement.GetProperty("timeout_seconds").GetInt32().Should().Be(45);
    }

    [Fact]
    public void TryParseFallbackToolCall_HermesJsonStillWorks()
    {
        var content = "Working... <tool_call>{\"name\": \"read_file\", \"arguments\": {\"path\": \"a.cs\"}}</tool_call>";
        var (hasTool, name, args) = OpenAiAdapter.TryParseFallbackToolCall(content);
        hasTool.Should().BeTrue();
        name.Should().Be("read_file");
        args.Should().Contain("a.cs");
    }

    [Theory]
    [InlineData("42", typeof(long))]
    [InlineData("3.5", typeof(double))]
    [InlineData("true", typeof(bool))]
    [InlineData("hello world", typeof(string))]
    public void CoerceXmlParamValue_TypesArePreserved(string raw, Type expected)
    {
        OpenAiAdapter.CoerceXmlParamValue(raw)!.GetType().Should().Be(expected);
    }

    // ---------- benign exit-code classification ----------

    [Fact]
    public async Task ExecuteToolLocallyAsync_RunCommand_AnnotatesBenignGrepMiss()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_tool_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "empty.txt");
            await File.WriteAllTextAsync(filePath, "nothing relevant here\n");
            var args = JsonSerializer.Serialize(new { command = $"grep zzz_no_match {filePath}" });
            var res = await OpenAiAdapter.ExecuteToolLocallyAsync("run_command", args, tempDir, CancellationToken.None);
            res.Should().Contain("Exit Code: 1");
            res.Should().Contain("NORMAL RESULT");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_RunCommand_DoesNotAnnotateRealFailures()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_tool_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var args = JsonSerializer.Serialize(new { command = "exit 1" });
            var res = await OpenAiAdapter.ExecuteToolLocallyAsync("run_command", args, tempDir, CancellationToken.None);
            res.Should().Contain("Exit Code: 1");
            res.Should().NotContain("NORMAL RESULT");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("grep foo x.txt", true)]
    [InlineData("/usr/bin/rg pattern .", true)]
    [InlineData("git grep pattern", true)]
    [InlineData("diff a b", true)]
    [InlineData("dotnet test", false)]
    [InlineData("echo done", false)]
    public void IsBenignExitOneCommand_Classification(string cmd, bool benign)
    {
        OpenAiAdapter.IsBenignExitOneCommand(cmd).Should().Be(benign);
    }

    // ---------- behavior profile application ----------

    [Fact]
    public void ApplyProfileSampling_QwenLoopback_SendsQwenNumbers_AndUnknownModelSendsNothing()
    {
        var qwen = AgentMesh.Core.Services.ModelBehaviorProfiles.Resolve("qwen3.8-flash-next", endpointIsLoopback: true);
        var body = new Dictionary<string, object?>();
        var sent = OpenAiAdapter.ApplyProfileSampling(body, qwen, "qwen3.8-flash-next");
        sent.Should().Contain(new[] { "temperature", "top_p", "top_k", "min_p" });
        body["top_k"].Should().Be(20L);           // integers stay integers on the wire
        body["temperature"].Should().Be(0.6);

        var unknown = AgentMesh.Core.Services.ModelBehaviorProfiles.Resolve("mystery-9000", endpointIsLoopback: true);
        var emptyBody = new Dictionary<string, object?>();
        OpenAiAdapter.ApplyProfileSampling(emptyBody, unknown, "mystery-9000").Should().BeEmpty();
        emptyBody.Should().BeEmpty();              // byte-for-byte the pre-registry request shape
    }

    [Fact]
    public void ParseRejectedRequestParameters_NeedsRejectionPhrase_AndOnlyOwnKeys()
    {
        var candidates = new List<string> { "top_k", "temperature" };
        OpenAiAdapter.ParseRejectedRequestParameters(
                "{\"error\":\"top_k: Extra inputs are not permitted\"}", candidates)
            .Should().BeEquivalentTo(new[] { "top_k" });
        // Same key named without a parameter-rejection phrase -> learn nothing (e.g. a context
        // error that happens to mention the word in prose).
        OpenAiAdapter.ParseRejectedRequestParameters(
                "context length exceeded near top_k", candidates)
            .Should().BeEmpty();
    }

    [Fact]
    public void MaybeAttachReasoning_EchoesField_WhenServerSeparated()
    {
        var profile = AgentMesh.Core.Services.ModelBehaviorProfiles.Resolve("qwen3.8-flash-next", endpointIsLoopback: true);
        var shape = new OpenAiAdapter.ReasoningShapeTracker();
        shape.NoteField();
        var msg = new OpenAiMessage { Role = "assistant", Content = "answer" };
        OpenAiAdapter.MaybeAttachReasoningToHistory(msg, "  because reasons  ", shape, profile, "qwen3.8-flash-next");
        msg.ReasoningContent.Should().Be("because reasons");
        msg.Content.Should().Be("answer");
    }

    [Fact]
    public void MaybeAttachReasoning_FallsBackToBalancedInline_WhenFieldRejected()
    {
        AgentMesh.Core.Services.ModelBehaviorProfiles.ClearLearnedState();
        try
        {
            AgentMesh.Core.Services.ModelBehaviorProfiles.RecordReasoningFieldRejected("m-inline");
            var profile = AgentMesh.Core.Services.ModelBehaviorProfiles.Resolve("m-inline", endpointIsLoopback: true);
            var shape = new OpenAiAdapter.ReasoningShapeTracker();
            shape.NoteField();
            var msg = new OpenAiMessage { Role = "assistant", Content = "answer" };
            OpenAiAdapter.MaybeAttachReasoningToHistory(msg, "deep thought", shape, profile, "m-inline");
            msg.ReasoningContent.Should().BeNull();
            msg.Content.Should().StartWith("<think>\ndeep thought\n</think>\n\n");
            msg.Content.Should().EndWith("answer");
        }
        finally
        {
            AgentMesh.Core.Services.ModelBehaviorProfiles.ClearLearnedState();
        }
    }

    [Fact]
    public void MaybeAttachReasoning_SkipsUnbalancedTruncatedReasoning()
    {
        var profile = AgentMesh.Core.Services.ModelBehaviorProfiles.Resolve("qwen3.8-flash-next", endpointIsLoopback: true);
        var shape = new OpenAiAdapter.ReasoningShapeTracker();
        shape.NoteField();
        AgentMesh.Core.Services.ModelBehaviorProfiles.ClearLearnedState();
        AgentMesh.Core.Services.ModelBehaviorProfiles.RecordReasoningFieldRejected("qwen3.8-flash-next"); // force inline path
        try
        {
            var msg = new OpenAiMessage { Role = "assistant", Content = "answer" };
            OpenAiAdapter.MaybeAttachReasoningToHistory(msg, "<think>truncated by stream death", shape, profile, "qwen3.8-flash-next");
            msg.ReasoningContent.Should().BeNull();
            msg.Content.Should().Be("answer"); // an unclosed think block would poison history
        }
        finally
        {
            AgentMesh.Core.Services.ModelBehaviorProfiles.ClearLearnedState();
        }
    }

    [Fact]
    public void Reasoning_SerializesOnlyWhenSet_AndCountsInEstimator()
    {
        var plain = new OpenAiMessage { Role = "assistant", Content = "x" };
        var plainDict = (IDictionary<string, object?>)plain.ToSerializableObject();
        plainDict.ContainsKey("reasoning_content").Should().BeFalse();

        var withReasoning = new OpenAiMessage { Role = "assistant", Content = "x", ReasoningContent = new string('y', 3600) };
        var dict = (IDictionary<string, object?>)withReasoning.ToSerializableObject();
        dict["reasoning_content"].Should().Be(new string('y', 3600));

        OpenAiAdapter.EstimateMessageTokens(withReasoning)
            .Should().BeGreaterThan(OpenAiAdapter.EstimateMessageTokens(plain) + 900);
    }

    // ---------- swap + intent guards ----------

    [Theory]
    [InlineData(503, "The model server (titanium) is offline right now", true)]
    [InlineData(500, "model loading, try again", true)]
    [InlineData(502, "Bad Gateway", false)]
    [InlineData(500, null, false)]
    public void IsModelServerSwapSignal_Detection(int status, string? body, bool expected)
    {
        OpenAiAdapter.IsModelServerSwapSignal(status, body).Should().Be(expected);
    }

    [Theory]
    [InlineData("Let me check the file again.", true)]
    [InlineData("I'll run the tests now and verify the fix.", true)]
    [InlineData("I ran the tests and all 214 passed. The PR is ready.", false)]
    [InlineData("Done. Should I merge it?", false)]
    [InlineData("", false)]
    public void LooksLikeUnexecutedIntent_Detection(string content, bool expected)
    {
        OpenAiAdapter.LooksLikeUnexecutedIntent(content).Should().Be(expected);
    }

    [Fact]
    public void ResolveSwapWaitBudget_PresetZeroDisables_DefaultIsTenMinutes()
    {
        OpenAiAdapter.ResolveSwapWaitBudgetSeconds(new AgentPresetConfig { SwapWaitSeconds = 0 }).Should().Be(0);
        OpenAiAdapter.ResolveSwapWaitBudgetSeconds(new AgentPresetConfig { SwapWaitSeconds = 120 }).Should().Be(120);
        OpenAiAdapter.ResolveSwapWaitBudgetSeconds(new AgentPresetConfig()).Should().BeGreaterThanOrEqualTo(0);
    }

    [Theory]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:9999/v1", true)]
    [InlineData("https://api.example.com/v1", false)]
    public async Task EnsureCompanionReady_NonSidecarProfiles_AlwaysReady(string endpoint, bool _)
    {
        // No sidecar configured => the pre-flight must be a no-op, for loopback AND remote endpoints.
        var preset = new AgentPresetConfig { Id = "test", Endpoint = endpoint };
        var (ready, guidance) = await OpenAiAdapter.EnsureCompanionReadyAsync(preset, endpoint, _ => Task.CompletedTask, CancellationToken.None);
        ready.Should().BeTrue();
        guidance.Should().BeNull();
    }

    [Fact]
    public async Task EnsureCompanionReady_LoopbackDeadPortWithoutCompanionCommand_NotReady()
    {
        var preset = new AgentPresetConfig
        {
            Id = "test",
            Sidecar = new AgentMesh.Core.Models.DriverSidecarConfig { Enabled = true, CompanionPort = 1, CompanionCommand = null, LoginCommand = "foo login" }
        };
        var (ready, guidance) = await OpenAiAdapter.EnsureCompanionReadyAsync(preset, "http://localhost:1/v1", _ => Task.CompletedTask, CancellationToken.None);
        ready.Should().BeFalse();
        guidance.Should().NotBeNull().And.Contain("foo login");
    }

    [Fact]
    public void BuildCompanionAuthHint_IsDerivedFromConfigNotHardcodedBinaries()
    {
        var withSidecar = new AgentPresetConfig
        {
            Sidecar = new AgentMesh.Core.Models.DriverSidecarConfig
            {
                Enabled = true,
                LoginCommand = "vendor-cli login",
                AuthCheckCommand = "vendor-cli status"
            }
        };
        var hint = OpenAiAdapter.BuildCompanionAuthHint(withSidecar);
        hint.Should().NotBeNull();
        hint.Should().Contain("vendor-cli login").And.Contain("vendor-cli status");
        hint.Should().NotContain("aip");

        OpenAiAdapter.BuildCompanionAuthHint(new AgentPresetConfig()).Should().BeNull();
    }

    [Fact]
    public async Task ExecutePromptAsync_WithUnreachableServer_EmitsDescriptiveErrorMessage()
    {
        var adapter = new OpenAiAdapter();
        var preset = new AgentPresetConfig
        {
            Id = "test-ollama",
            Name = "Local Ollama",
            AdapterType = "openai",
            Endpoint = "http://127.0.0.1:59998/v1"
        };

        var promptEv = new PromptReceivedEvent
        {
            SessionId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            AgentId = "test-ollama",
            Prompt = "Hello world",
            Sender = "operator"
        };

        var events = new List<TrajectoryEventPayload>();
        await adapter.ExecutePromptAsync(preset, promptEv, ev =>
        {
            events.Add(ev);
            return Task.CompletedTask;
        }, CancellationToken.None);

        events.Should().Contain(e => e.EventType == "thought" && e.Content.Contains("Connecting to OpenAI-compatible server"));
        events.Should().Contain(e => e.EventType == "response" && e.Content.Contains("Failed to connect to OpenAI-compatible endpoint"));
    }

    [Fact]
    public void ConfigStore_PopulateFromRemote_ExtractsOpenAiSettingsCorrectly()
    {
        var configStore = new ConfigStore();
        var preset = new AgentPresetConfig
        {
            Id = "test-openai-driver",
            Name = "Agent test-openai-driver",
            AdapterType = "generic"
        };

        var remote = new RemoteAgentDto(
            Id: "test-openai-driver",
            Name: "Test OpenAI Driver",
            Role: "Autonomous Agent",
            Status: "offline",
            SessionCount: 1,
            LastHeartbeatAt: DateTimeOffset.UtcNow,
            CapabilitiesJson: JsonSerializer.Serialize(new
            {
                adapter = "openai",
                endpoint = "http://127.0.0.1:11435/v1",
                apiKey = "sk-test-key",
                defaultModel = "qwen3.8-flash-next"
            })
        );

        configStore.PopulateFromRemote(preset, remote);

        preset.Name.Should().Be("Test OpenAI Driver");
        preset.Role.Should().Be("Autonomous Agent");
        preset.AdapterType.Should().Be("openai");
        preset.Endpoint.Should().Be("http://127.0.0.1:11435/v1");
        preset.ApiKey.Should().Be("sk-test-key");
        preset.DefaultModel.Should().Be("qwen3.8-flash-next");
        preset.Capabilities.Should().Contain("run_command");
    }

    [Fact]
    public void MeshClient_SerializePresetCapabilities_SerializesOpenAiMetadata()
    {
        var preset = new AgentPresetConfig
        {
            Id = "test-openai-driver",
            Name = "Test OpenAI Driver",
            AdapterType = "openai",
            Endpoint = "http://127.0.0.1:11435/v1",
            ApiKey = "sk-key",
            DefaultModel = "qwen3.8-flash-next",
            Capabilities = new List<string> { "run_command", "read_file" }
        };

        var json = MeshClient.SerializePresetCapabilities(preset);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("adapter").GetString().Should().Be("openai");
        doc.RootElement.GetProperty("endpoint").GetString().Should().Be("http://127.0.0.1:11435/v1");
        doc.RootElement.GetProperty("apiKey").GetString().Should().Be("sk-key");
        doc.RootElement.GetProperty("defaultModel").GetString().Should().Be("qwen3.8-flash-next");
    }

    [Fact]
    public void ResolveTimeoutSeconds_UsesPresetOrEnvOrDefault()
    {
        var presetWithTimeout = new AgentPresetConfig { TimeoutSeconds = 900 };
        OpenAiAdapter.ResolveTimeoutSeconds(presetWithTimeout).Should().Be(900);

        var presetDefault = new AgentPresetConfig();
        OpenAiAdapter.ResolveTimeoutSeconds(presetDefault).Should().Be(600); // 10 minutes default
    }

    [Fact]
    public void ResolveModelContextLimit_ResolvesKnownModelsAndOptions()
    {
        AgentMesh.Core.Services.ModelContextLimits.ClearLearnedLimits();
        OpenAiAdapter.ResolveModelContextLimit("qwen3.8-flash-next", null).Should().Be(524_288);
        OpenAiAdapter.ResolveModelContextLimit("llama3.3", null).Should().Be(131_072);
        OpenAiAdapter.ResolveModelContextLimit("qwen2.5-coder", null).Should().Be(131_072);
        OpenAiAdapter.ResolveModelContextLimit("custom", "256k").Should().Be(262_144);
        OpenAiAdapter.ResolveModelContextLimit("custom", "512k").Should().Be(524_288);
        OpenAiAdapter.ResolveModelContextLimit("custom", "1m").Should().Be(1_048_576);
        OpenAiAdapter.ResolveModelContextLimit("unknown", "default").Should().Be(1_048_576);

        AgentMesh.Core.Services.ModelContextLimits.RecordLearnedLimit("unknown-custom-model", 262_144);
        OpenAiAdapter.ResolveModelContextLimit("unknown-custom-model", "default").Should().Be(262_144);
        AgentMesh.Core.Services.ModelContextLimits.ClearLearnedLimits();
    }

    [Fact]
    public void ParseContextWindowError_ParsesLiteLlmAndOpenAiFormats()
    {
        var litellmError = "{\"error\":{\"message\":\"litellm.BadRequestError: OpenAIException - request exceeds the context window: prompt (548337 tokens) leaves no room to answer in the context (524288); requests are never truncated. Received Model Group=qwen3.8-flash-next\\nAvailable Model Group Fallbacks=None\",\"type\":\"invalid_request_error\",\"param\":null,\"code\":\"400\"}}";
        var (isLiteLlm, promptTokens, contextLimit) = OpenAiAdapter.ParseContextWindowError(litellmError);
        isLiteLlm.Should().BeTrue();
        promptTokens.Should().Be(548_337);
        contextLimit.Should().Be(524_288);

        var openAiError = "maximum context length is 131072 tokens. However, your messages resulted in 140000 tokens.";
        var (isOpenAi, pt2, cl2) = OpenAiAdapter.ParseContextWindowError(openAiError);
        isOpenAi.Should().BeTrue();
        pt2.Should().Be(140_000);
        cl2.Should().Be(131_072);

        var normalError = "Invalid model identifier";
        var (isNormal, _, _) = OpenAiAdapter.ParseContextWindowError(normalError);
        isNormal.Should().BeFalse();
    }

    [Fact]
    public void CompactHistory_TruncatesOlderToolOutputsAndPrunesEarlierTurns()
    {
        var history = new List<OpenAiMessage>
        {
            new() { Role = "system", Content = "You are an autonomous engineer." },
            new() { Role = "user", Content = "Please inspect files." },
            new() { Role = "assistant", Content = "I will read the big file.", ToolCalls = new List<OpenAiToolCall> { new() { Id = "call_1", Function = new() { Name = "read_file" } } } },
            new() { Role = "tool", ToolCallId = "call_1", Content = new string('A', 5000) },
            new() { Role = "assistant", Content = "I read it, now editing." },
            new() { Role = "user", Content = "Continue your work." },
            new() { Role = "assistant", Content = "Running final build.", ToolCalls = new List<OpenAiToolCall> { new() { Id = "call_2", Function = new() { Name = "run_command" } } } },
            new() { Role = "tool", ToolCallId = "call_2", Content = "Exit Code: 0" }
        };

        var initialTokens = OpenAiAdapter.EstimateTotalTokens(history);
        initialTokens.Should().BeGreaterThan(1000);

        // Compact to 400 tokens
        var compacted = OpenAiAdapter.CompactHistory(history, 400);
        compacted.Should().BeTrue();

        // System prompt preserved
        history[0].Role.Should().Be("system");
        history[0].Content.Should().Contain("autonomous engineer");

        // Older tool output truncated
        var compactedTokens = OpenAiAdapter.EstimateTotalTokens(history);
        compactedTokens.Should().BeLessThan(initialTokens);
    }

    [Fact]
    public void TruncateToolResult_CapsLargeResultsAtHeadAndTail()
    {
        var small = "Small output";
        OpenAiAdapter.TruncateToolResult(small, 50_000).Should().Be(small);

        var large = new string('X', 60_000);
        var truncated = OpenAiAdapter.TruncateToolResult(large, 50_000);
        truncated.Length.Should().BeLessThan(large.Length);
        truncated.Should().Contain("Output truncated");
        truncated.Should().StartWith(new string('X', 100));
        truncated.Should().EndWith(new string('X', 100));
    }

    [Fact]
    public void IsRetryableStatusCode_IdentifiesTransientErrors()
    {
        OpenAiAdapter.IsRetryableStatusCode(429).Should().BeTrue();
        OpenAiAdapter.IsRetryableStatusCode(500).Should().BeTrue();
        OpenAiAdapter.IsRetryableStatusCode(502).Should().BeTrue();
        OpenAiAdapter.IsRetryableStatusCode(503).Should().BeTrue();
        OpenAiAdapter.IsRetryableStatusCode(504).Should().BeTrue();

        OpenAiAdapter.IsRetryableStatusCode(400).Should().BeFalse();
        OpenAiAdapter.IsRetryableStatusCode(401).Should().BeFalse();
        OpenAiAdapter.IsRetryableStatusCode(403).Should().BeFalse();
        OpenAiAdapter.IsRetryableStatusCode(404).Should().BeFalse();
        OpenAiAdapter.IsRetryableStatusCode(200).Should().BeFalse();
    }

    [Fact]
    public async Task ExecutePromptAsync_WithTransient503_RetriesAndSucceeds()
    {
        var adapter = new OpenAiAdapter();
        var preset = new AgentPresetConfig
        {
            Id = "test-openai-driver",
            Name = "Test OpenAI Driver",
            AdapterType = "openai",
            Endpoint = "http://127.0.0.1:11435/v1",
            DefaultModel = "qwen3.8-flash-next",
            MaxRetries = 2,
            RetryDelayMilliseconds = 1
        };

        var promptEv = new PromptReceivedEvent
        {
            SessionId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            AgentId = "test-openai-driver",
            Prompt = "Hello after retry",
            Sender = "operator"
        };

        var attempt = 0;
        using var mockClient = new HttpClient(new MockHttpMessageHandler(_ =>
        {
            attempt++;
            if (attempt == 1)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    ReasonPhrase = "Service Unavailable",
                    Content = new StringContent("Model loading into VRAM...")
                };
            }

            var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"Hello back!\"}}]}\n\ndata: [DONE]\n\n";
            var res = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream")
            };
            return res;
        }));

        OpenAiAdapter.SetHttpClientForTest(mockClient);
        try
        {
            var events = new List<TrajectoryEventPayload>();
            await adapter.ExecutePromptAsync(preset, promptEv, ev =>
            {
                events.Add(ev);
                return Task.CompletedTask;
            }, CancellationToken.None);

            attempt.Should().Be(2);
            events.Should().Contain(e => e.EventType == "thought" && e.Content.Contains("HTTP 503"));
            events.Should().Contain(e => e.EventType == "response" && e.Content.Contains("Hello back!"));
        }
        finally
        {
            OpenAiAdapter.ResetHttpClientForTest();
        }
    }

    [Fact]
    public async Task ExecutePromptAsync_WithContextLimit400_CompactsHistoryAndRetries()
    {
        var adapter = new OpenAiAdapter();
        var preset = new AgentPresetConfig
        {
            Id = "test-openai-driver",
            Name = "Test OpenAI Driver",
            AdapterType = "openai",
            Endpoint = "http://127.0.0.1:11435/v1",
            DefaultModel = "qwen3.8-flash-next",
            MaxRetries = 2,
            RetryDelayMilliseconds = 1
        };

        var sessionId = Guid.NewGuid();
        var promptEv = new PromptReceivedEvent
        {
            SessionId = sessionId,
            MessageId = Guid.NewGuid(),
            AgentId = "test-openai-driver",
            Prompt = "Please help me",
            Sender = "operator"
        };

        var attempt = 0;
        using var mockClient = new HttpClient(new MockHttpMessageHandler(_ =>
        {
            attempt++;
            if (attempt == 1)
            {
                var errJson = "{\"error\":{\"message\":\"litellm.BadRequestError: OpenAIException - request exceeds the context window: prompt (548337 tokens) leaves no room to answer in the context (524288); requests are never truncated.\"}}";
                return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
                {
                    ReasonPhrase = "Bad Request",
                    Content = new StringContent(errJson)
                };
            }

            var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"Context recovered!\"}}]}\n\ndata: [DONE]\n\n";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream")
            };
        }));

        OpenAiAdapter.SetHttpClientForTest(mockClient);
        try
        {
            var events = new List<TrajectoryEventPayload>();
            await adapter.ExecutePromptAsync(preset, promptEv, ev =>
            {
                events.Add(ev);
                return Task.CompletedTask;
            }, CancellationToken.None);

            attempt.Should().Be(2);
            events.Should().Contain(e => e.EventType == "thought" && e.Content.Contains("Context window exceeded"));
            events.Should().Contain(e => e.EventType == "response" && e.Content.Contains("Context recovered!"));
        }
        finally
        {
            OpenAiAdapter.ResetHttpClientForTest();
            OpenAiAdapter.ClearSessionHistory(sessionId);
        }
    }

    [Fact]
    public void ResolveMaxSteps_ReturnsPresetValueWhenConfigured()
    {
        var preset = new AgentPresetConfig { MaxSteps = 50 };
        OpenAiAdapter.ResolveMaxSteps(preset).Should().Be(50);
    }

    [Fact]
    public void ResolveMaxSteps_ReturnsDefaultWhenUnset()
    {
        var preset = new AgentPresetConfig();
        var origEnv = Environment.GetEnvironmentVariable("OPENAI_MAX_STEPS");
        var origMeshEnv = Environment.GetEnvironmentVariable("AGENTMESH_MAX_STEPS");
        var origAgentEnv = Environment.GetEnvironmentVariable("AGENT_MAX_STEPS");
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_MAX_STEPS", null);
            Environment.SetEnvironmentVariable("AGENTMESH_MAX_STEPS", null);
            Environment.SetEnvironmentVariable("AGENT_MAX_STEPS", null);
            OpenAiAdapter.ResolveMaxSteps(preset, isDiscoveryPhase: false).Should().Be(50);
            OpenAiAdapter.ResolveMaxSteps(preset, isDiscoveryPhase: true).Should().Be(50);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_MAX_STEPS", origEnv);
            Environment.SetEnvironmentVariable("AGENTMESH_MAX_STEPS", origMeshEnv);
            Environment.SetEnvironmentVariable("AGENT_MAX_STEPS", origAgentEnv);
        }
    }

    [Fact]
    public void ResolveMaxSteps_ReturnsEnvVarWhenSet()
    {
        var preset = new AgentPresetConfig();
        var origEnv = Environment.GetEnvironmentVariable("OPENAI_MAX_STEPS");
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_MAX_STEPS", "75");
            OpenAiAdapter.ResolveMaxSteps(preset).Should().Be(75);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_MAX_STEPS", origEnv);
        }
    }

    [Fact]
    public async Task ExecutePromptAsync_WhenMaxStepsReached_EmitsThoughtAndSummaryResponse()
    {
        var adapter = new OpenAiAdapter();
        var preset = new AgentPresetConfig
        {
            Id = "test-openai-driver",
            Name = "Test OpenAI Driver",
            AdapterType = "openai",
            Endpoint = "http://127.0.0.1:11435/v1",
            MaxSteps = 2 // Low step limit for test
        };

        var sessionId = Guid.NewGuid();
        var promptEv = new PromptReceivedEvent
        {
            SessionId = sessionId,
            MessageId = Guid.NewGuid(),
            AgentId = "test-openai-driver",
            Prompt = "Run tasks",
            Sender = "operator"
        };

        var requestCount = 0;
        using var mockClient = new HttpClient(new MockHttpMessageHandler(req =>
        {
            requestCount++;
            if (requestCount <= 2)
            {
                // Return a tool call for each step to consume maxSteps
                var toolCallSse = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_" + requestCount + "\",\"function\":{\"name\":\"run_command\",\"arguments\":\"{\\\"command\\\":\\\"echo step" + requestCount + "\\\"}\"}}]}}]}\n\ndata: [DONE]\n\n";
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(toolCallSse, System.Text.Encoding.UTF8, "text/event-stream")
                };
            }

            // Summary request (stream = false)
            var summaryJson = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Completed 2 steps of tool execution. Ready to continue.\"}}]}";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(summaryJson, System.Text.Encoding.UTF8, "application/json")
            };
        }));

        OpenAiAdapter.SetHttpClientForTest(mockClient);
        try
        {
            var events = new List<TrajectoryEventPayload>();
            await adapter.ExecutePromptAsync(preset, promptEv, ev =>
            {
                events.Add(ev);
                return Task.CompletedTask;
            }, CancellationToken.None);

            // Verify that it executed 2 tool calls, then hit max steps
            events.Where(e => e.EventType == "tool_call").Should().HaveCount(2);
            // Must emit thought about reaching progress checkpoint
            events.Should().Contain(e => e.EventType == "thought" && e.Content.Contains("Reached progress checkpoint (2 steps)"));
            // Must emit final response so session does NOT hang in thinking
            var finalResp = events.FirstOrDefault(e => e.EventType == "response");
            finalResp.Should().NotBeNull();
            finalResp!.Content.Should().Contain("Progress Checkpoint (2 steps)");
            finalResp.Content.Should().Contain("Completed 2 steps");
        }
        finally
        {
            OpenAiAdapter.ResetHttpClientForTest();
            OpenAiAdapter.ClearSessionHistory(sessionId);
        }
    }

    [Fact]
    public async Task ExecutePromptAsync_WhenFinalContentEmpty_EmitsNonEmptyFallbackResponse()
    {
        var adapter = new OpenAiAdapter();
        var preset = new AgentPresetConfig
        {
            Id = "test-openai-driver",
            Name = "Test OpenAI Driver",
            AdapterType = "openai",
            Endpoint = "http://127.0.0.1:11435/v1"
        };

        var sessionId = Guid.NewGuid();
        var promptEv = new PromptReceivedEvent
        {
            SessionId = sessionId,
            MessageId = Guid.NewGuid(),
            AgentId = "test-openai-driver",
            Prompt = "Hello",
            Sender = "operator"
        };

        using var mockClient = new HttpClient(new MockHttpMessageHandler(_ =>
        {
            // Model returns empty content delta, only reasoning
            var sse = "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"I am thinking deeply about this...\"}}]}\n\ndata: [DONE]\n\n";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream")
            };
        }));

        OpenAiAdapter.SetHttpClientForTest(mockClient);
        try
        {
            var events = new List<TrajectoryEventPayload>();
            await adapter.ExecutePromptAsync(preset, promptEv, ev =>
            {
                events.Add(ev);
                return Task.CompletedTask;
            }, CancellationToken.None);

            var resp = events.FirstOrDefault(e => e.EventType == "response");
            resp.Should().NotBeNull();
            resp!.Content.Should().NotBeNullOrWhiteSpace();
            resp.Content.Should().Be("I am thinking deeply about this...");
        }
        finally
        {
            OpenAiAdapter.ResetHttpClientForTest();
            OpenAiAdapter.ClearSessionHistory(sessionId);
        }
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_ReadFile_OnImageFile_ReturnsImageIndicatorWithoutBinaryGarbage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_img_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var imagePath = Path.Combine(tempDir, "mock_ui.png");
            // Standard PNG header bytes with null bytes
            var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52 };
            await File.WriteAllBytesAsync(imagePath, pngBytes);

            var args = JsonSerializer.Serialize(new { path = "mock_ui.png" });
            var result = await OpenAiAdapter.ExecuteToolLocallyAsync("read_file", args, tempDir, CancellationToken.None);

            result.Should().Contain("[Image file: 'mock_ui.png'");
            result.Should().Contain("format: PNG");
            result.Should().Contain("cannot be read as plain text");
            result.Should().Contain("Do NOT attempt to install OCR packages");
            result.Should().NotContain("\0");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_RethrowsOperationCanceledException()
    {
        // Was hardcoded to "/workspace", which only exists inside a persona
        // container running as root. On a CI runner (non-root, repo checked out
        // under /home/runner) that path neither exists nor can be created, so
        // the tool's Directory.CreateDirectory threw UnauthorizedAccessException
        // and the generic catch turned it into a "Tool execution error" *string*
        // instead of propagating cancellation. The test passed locally and failed
        // in CI. A temp dir keeps it hermetic.
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_cancel_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var args = JsonSerializer.Serialize(new { command = "sleep 10" });
            var act = async () => await OpenAiAdapter.ExecuteToolLocallyAsync("run_command", args, tempDir, cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_ReadFile_OnBinaryFile_ReturnsBinaryIndicatorWithoutNullBytes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_bin_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var binPath = Path.Combine(tempDir, "binary.dat");
            var binBytes = new byte[] { 0x01, 0x02, 0x00, 0x03, 0x04, 0x00, 0x05 };
            await File.WriteAllBytesAsync(binPath, binBytes);

            var args = JsonSerializer.Serialize(new { path = "binary.dat" });
            var result = await OpenAiAdapter.ExecuteToolLocallyAsync("read_file", args, tempDir, CancellationToken.None);

            result.Should().Contain("[Binary file: 'binary.dat'");
            result.Should().Contain("cannot be read as plain text");
            result.Should().NotContain("\0");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_ToolResultSanitizesNullBytes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_null_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            // Execute a bash echo with null byte
            var args = JsonSerializer.Serialize(new { command = "printf 'hello\\x00world\\n'" });
            var result = await OpenAiAdapter.ExecuteToolLocallyAsync("run_command", args, tempDir, CancellationToken.None);

            result.Should().Contain("helloworld");
            result.Should().NotContain("\0");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void OpenAiMessage_ToSerializableObject_SerializesMultimodalContentParts()
    {
        var msg = new OpenAiMessage
        {
            Role = "user",
            Content = "Look at this screenshot",
            ContentParts = new List<object>
            {
                new OpenAiTextContentPart { Type = "text", Text = "Look at this screenshot" },
                new OpenAiImageUrlContentPart
                {
                    Type = "image_url",
                    ImageUrl = new OpenAiImageUrl { Url = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUg==" }
                }
            }
        };

        var serialized = JsonSerializer.Serialize(msg.ToSerializableObject());
        serialized.Should().Contain("\"role\":\"user\"");
        serialized.Should().Contain("\"type\":\"text\"");
        serialized.Should().Contain("\"type\":\"image_url\"");
        serialized.Should().Contain("\"url\":\"data:image/png;base64,iVBORw0KGgoAAAANSUhEUg==\"");
    }

    [Fact]
    public async Task ExecutePromptAsync_WithImageAttachment_IncludesMultimodalImageContentPart()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_mm_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var sessionId = Guid.NewGuid();
        string? capturedBody = null;

        var handler = new MockHttpMessageHandler(req =>
        {
            capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"I see the image clearly.\"}}]}\n\ndata: [DONE]\n\n";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream")
            };
        });

        OpenAiAdapter.SetHttpClientForTest(new HttpClient(handler));
        try
        {
            var imagePath = Path.Combine(tempDir, "sample.png");
            var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52 };
            await File.WriteAllBytesAsync(imagePath, pngBytes);

            var adapter = new OpenAiAdapter();
            var preset = new AgentPresetConfig
            {
                Id = "test-openai-driver",
                Name = "Test OpenAI Driver",
                AdapterType = "openai",
                Endpoint = "http://127.0.0.1:11435/v1",
                DefaultModel = "qwen3.8-flash-next"
            };

            var promptEv = new PromptReceivedEvent
            {
                SessionId = sessionId,
                MessageId = Guid.NewGuid(),
                AgentId = "test-openai-driver",
                Prompt = "What is in this image?",
                Sender = "operator",
                WorkingDirectory = tempDir,
                Attachments = new List<AttachmentItem>
                {
                    new AttachmentItem
                    {
                        Id = "att1",
                        FileName = "sample.png",
                        ContentType = "image/png",
                        SizeBytes = pngBytes.Length,
                        LocalFilePath = imagePath
                    }
                }
            };

            var events = new List<TrajectoryEventPayload>();
            await adapter.ExecutePromptAsync(preset, promptEv, ev => { events.Add(ev); return Task.CompletedTask; }, CancellationToken.None);

            capturedBody.Should().NotBeNull();
            capturedBody.Should().Contain("\"type\":\"image_url\"");
            capturedBody.Should().Contain("data:image/png;base64,");
            events.Should().Contain(e => e.Content.Contains("I see the image clearly."));
        }
        finally
        {
            OpenAiAdapter.ResetHttpClientForTest();
            OpenAiAdapter.ClearSessionHistory(sessionId);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void OpenAiAdapter_HistoryDiskPersistence_RoundTripsAcrossRestarts()
    {
        var sessionId = Guid.NewGuid();
        var tempHistoryDir = Path.Combine(Path.GetTempPath(), $"openai-hist-{Guid.NewGuid()}");
        var origDir = OpenAiAdapter.HistoryDirectoryPath;
        OpenAiAdapter.HistoryDirectoryPath = tempHistoryDir;

        try
        {
            var initialHistory = new List<OpenAiMessage>
            {
                new OpenAiMessage { Role = "system", Content = "You are a senior tech lead." },
                new OpenAiMessage { Role = "user", Content = "Inspect the codebase." },
                new OpenAiMessage { Role = "assistant", Content = "Inspected." }
            };

            OpenAiAdapter.SaveHistoryToDisk(sessionId, initialHistory);

            // Simulate full memory wipe / CLI process restart
            OpenAiAdapter.ClearSessionHistory(sessionId);

            // Re-save to disk for restoration test
            OpenAiAdapter.SaveHistoryToDisk(sessionId, initialHistory);

            var restored = OpenAiAdapter.GetSessionHistory(sessionId);
            restored.Should().NotBeNull();
            restored.Should().HaveCount(3);
            restored![0].Content.Should().Be("You are a senior tech lead.");
            restored[1].Content.Should().Be("Inspect the codebase.");
            restored[2].Content.Should().Be("Inspected.");
        }
        finally
        {
            OpenAiAdapter.ClearSessionHistory(sessionId);
            OpenAiAdapter.HistoryDirectoryPath = origDir;
            if (Directory.Exists(tempHistoryDir)) Directory.Delete(tempHistoryDir, true);
        }
    }

    [Fact]
    public void ActiveCommandRegistry_AppendAndGetLiveOutput_TracksRunningOutput()
    {
        var sessionId = Guid.NewGuid();
        var commandId = "cmd-live-test";
        using var proc = new Process();
        using var cts = new CancellationTokenSource();

        using var reg = ActiveCommandRegistry.Register(sessionId, commandId, proc, cts);

        ActiveCommandRegistry.AppendOutput(sessionId, commandId, "Compiling project...\n");
        ActiveCommandRegistry.AppendOutput(sessionId, commandId, "Build succeeded.\n");

        var liveOutput = ActiveCommandRegistry.GetLiveOutput(sessionId, commandId);
        liveOutput.Should().NotBeNull();
        liveOutput.Should().Contain("Compiling project...");
        liveOutput.Should().Contain("Build succeeded.");
    }

    [Fact]
    public void ResolveMaxSteps_HonorsPresetExplicitValue_OverDefaultsAndDiscovery()
    {
        var preset = new AgentPresetConfig { MaxSteps = 42 };
        OpenAiAdapter.ResolveMaxSteps(preset, isDiscoveryPhase: true).Should().Be(42);
        OpenAiAdapter.ResolveMaxSteps(preset, isDiscoveryPhase: false).Should().Be(42);
    }

    [Fact]
    public void ResolveMaxSteps_HonorsGlobalDefault_WhenPresetUnset()
    {
        var preset = new AgentPresetConfig { MaxSteps = null };
        OpenAiAdapter.ResolveMaxSteps(preset, isDiscoveryPhase: false, globalDefault: 35).Should().Be(35);
        OpenAiAdapter.ResolveMaxSteps(preset, isDiscoveryPhase: true, globalDefault: 35).Should().Be(35);
    }

    [Fact]
    public void ResolveMaxSteps_FallsBackTo50_RegardlessOfPhase()
    {
        var preset = new AgentPresetConfig { MaxSteps = null };
        OpenAiAdapter.ResolveMaxSteps(preset, isDiscoveryPhase: true).Should().Be(50);
        OpenAiAdapter.ResolveMaxSteps(preset, isDiscoveryPhase: false).Should().Be(50);
    }

    [Fact]
    public void GetToolDefinitions_IsFrozen_SchemaStableAcrossPhases()
    {
        // The tool array sits inside the cached system prefix; swapping it at stage
        // transitions busts the prefix cache and confuses models mid-session. The schema is
        // therefore a single fixed list, and stage enforcement moved to the executor
        // (see ExecuteToolLocallyAsync_RejectsMutations_DuringDiscovery).
        var tools = OpenAiAdapter.GetToolDefinitions();
        var names = tools
            .Select(t => ((dynamic)t).function.name as string)
            .ToList();

        names.Should().Contain("run_command");
        names.Should().Contain("read_file");
        names.Should().Contain("ask_question");
        names.Should().Contain("manage_task");
        names.Should().Contain("post_job_message");
        names.Should().Contain("save_memory");

        names.Should().Contain("write_file", "mutating tools stay in the frozen schema; the executor refuses them during discovery");
        names.Should().Contain("edit_file", "mutating tools stay in the frozen schema; the executor refuses them during discovery");
        names.Should().Contain("open_pull_request", "mutating tools stay in the frozen schema; the executor refuses them during discovery");
    }

    [Theory]
    [InlineData("git commit -m \"fix\"", true)]
    [InlineData("git push origin main", true)]
    [InlineData("gh pr create --title \"PR\"", true)]
    [InlineData("git rebase main", true)]
    [InlineData("git merge feature", true)]
    [InlineData("git cherry-pick 12345", true)]
    [InlineData("git status", false)]
    [InlineData("git log -n 5", false)]
    [InlineData("git diff", false)]
    [InlineData("git branch -a", false)]
    [InlineData("dotnet test", false)]
    public void IsMutatingGitCommand_CorrectlyIdentifiesMutations(string cmd, bool expectedMutating)
    {
        OpenAiAdapter.IsMutatingGitCommand(cmd).Should().Be(expectedMutating);
    }

    [Theory]
    [InlineData("read_file", "{\"path\":\"a.cs\"}", true)]
    [InlineData("ask_question", "{}", true)]
    [InlineData("save_memory", "{}", true)]
    [InlineData("run_command", "{\"command\":\"git status\"}", true)]
    [InlineData("run_command", "{\"command\":\"git log -n 5\"}", true)]
    [InlineData("run_command", "{\"command\":\"git diff\"}", true)]
    [InlineData("write_file", "{\"path\":\"a.cs\"}", false)]
    [InlineData("edit_file", "{\"path\":\"a.cs\"}", false)]
    [InlineData("run_command", "{\"command\":\"dotnet build\"}", false)]
    [InlineData("manage_task", "{\"action\":\"set_checklist\"}", false)]
    public void IsReadOnlyTool_CategorizesOperationsAccurately(string toolName, string argsJson, bool expectedReadOnly)
    {
        OpenAiAdapter.IsReadOnlyTool(toolName, argsJson).Should().Be(expectedReadOnly);
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_RejectsMutations_DuringDiscovery()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mesh-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var writeFileRes = await OpenAiAdapter.ExecuteToolLocallyAsync(
                "write_file",
                "{\"path\":\"test.txt\",\"content\":\"hello\"}",
                tempDir,
                CancellationToken.None,
                isDiscoveryPhase: true);

            writeFileRes.Should().Contain("File modifications are forbidden during Stage 1 Discovery");

            var gitCommitRes = await OpenAiAdapter.ExecuteToolLocallyAsync(
                "run_command",
                "{\"command\":\"git commit -m 'early'\"}",
                tempDir,
                CancellationToken.None,
                isDiscoveryPhase: true);

            gitCommitRes.Should().Contain("forbidden during Stage 1 Discovery");

            var ghPrRes = await OpenAiAdapter.ExecuteToolLocallyAsync(
                "run_command",
                "{\"command\":\"gh pr create --title 'early'\"}",
                tempDir,
                CancellationToken.None,
                isDiscoveryPhase: true);

            ghPrRes.Should().Contain("forbidden during Stage 1 Discovery");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("Error: file not found", true)]
    [InlineData("Error writing file", true)]
    [InlineData("Tool execution error: command exited with code 1", true)]
    [InlineData("An error occurred invoking 'open_pull_request'.", true)]
    [InlineData("""{"success":false,"error":"missing_argument"}""", true)]
    [InlineData("""{"isError":true,"content":[{"text":"failed"}]}""", true)]
    [InlineData("""{"error":"unauthorized"}""", true)]
    [InlineData("PR #4242 opened: https://github.com/ckchessmaster/agent-mesh/pull/4242", false)]
    [InlineData("""{"success":true,"pr_number":4242}""", false)]
    [InlineData("Successfully wrote 120 bytes to test.txt", false)]
    [InlineData("", true)]
    [InlineData(null, true)]
    public void IsToolResultError_DetectsFailuresCorrectly(string? output, bool expectedIsError)
    {
        var isError = OpenAiAdapter.IsToolResultError(output);
        isError.Should().Be(expectedIsError);
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_WriteToFileAlias_And_CaseInsensitiveArgs_Succeeds()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_alias_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var args = JsonSerializer.Serialize(new
            {
                TargetFile = "aliased_output.txt",
                CodeContent = "Hello from write_to_file tool alias"
            });
            var res = await OpenAiAdapter.ExecuteToolLocallyAsync("write_to_file", args, tempDir, CancellationToken.None);
            res.Should().Contain("Successfully wrote");

            var written = await File.ReadAllTextAsync(Path.Combine(tempDir, "aliased_output.txt"));
            written.Should().Be("Hello from write_to_file tool alias");

            // Now test replace_file_content alias
            var editArgs = JsonSerializer.Serialize(new
            {
                TargetFile = "aliased_output.txt",
                TargetContent = "Hello from write_to_file",
                ReplacementContent = "Replaced in replace_file_content"
            });
            var editRes = await OpenAiAdapter.ExecuteToolLocallyAsync("replace_file_content", editArgs, tempDir, CancellationToken.None);
            editRes.Should().Contain("Successfully replaced");

            var updated = await File.ReadAllTextAsync(Path.Combine(tempDir, "aliased_output.txt"));
            updated.Should().Be("Replaced in replace_file_content tool alias");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteToolLocallyAsync_AskQuestion_ReturnsNotedResponse()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agentmesh_ask_test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var args = JsonSerializer.Serialize(new
            {
                question = "Should we enable strict linting?",
                options = new[] { "Yes", "No" }
            });
            var res = await OpenAiAdapter.ExecuteToolLocallyAsync("ask_question", args, tempDir, CancellationToken.None);
            res.Should().Contain("Question submitted to operator");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void OpenAiAdapter_GetToolDefinitions_OpenPullRequestSchema_ContainsSummary3RAndBranchName()
    {
        var tools = OpenAiAdapter.GetToolDefinitions();
        var prTool = tools.FirstOrDefault(t =>
        {
            var json = JsonSerializer.Serialize(t);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("function").GetProperty("name").GetString() == "open_pull_request";
        });

        prTool.Should().NotBeNull();
        var prJson = JsonSerializer.Serialize(prTool);
        using var prDoc = JsonDocument.Parse(prJson);
        var props = prDoc.RootElement.GetProperty("function").GetProperty("parameters").GetProperty("properties");

        props.TryGetProperty("summary_3r", out var s3r).Should().BeTrue();
        props.TryGetProperty("branch_name", out var bn).Should().BeTrue();
        props.TryGetProperty("title", out var t).Should().BeTrue();
    }

    [Fact]
    public void OpenAiAdapter_GetToolDefinitions_SaveMemorySchema_ContainsValueAndCanonicalCategories()
    {
        var tools = OpenAiAdapter.GetToolDefinitions();
        var memTool = tools.FirstOrDefault(t =>
        {
            var json = JsonSerializer.Serialize(t);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("function").GetProperty("name").GetString() == "save_memory";
        });

        memTool.Should().NotBeNull();
        var memJson = JsonSerializer.Serialize(memTool);
        using var memDoc = JsonDocument.Parse(memJson);
        var props = memDoc.RootElement.GetProperty("function").GetProperty("parameters").GetProperty("properties");

        props.TryGetProperty("value", out var val).Should().BeTrue();
        props.TryGetProperty("key", out var k).Should().BeTrue();
        props.TryGetProperty("category", out var cat).Should().BeTrue();
        props.TryGetProperty("is_global", out var ig).Should().BeTrue();

        var catEnums = cat.GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToList();
        catEnums.Should().Contain(AgentMemoryCategories.Environment);
        catEnums.Should().Contain(AgentMemoryCategories.BuildAndTest);
        catEnums.Should().Contain(AgentMemoryCategories.CodebaseGotcha);
    }

    [Theory]
    [InlineData("""{"requires_reply": true}""", true)]
    [InlineData("""{"requires_reply": "true"}""", true)]
    [InlineData("""{"requires_reply": "True"}""", true)]
    [InlineData("""{"requiresReply": true}""", true)]
    [InlineData("""{"requiresReply": "true"}""", true)]
    [InlineData("""{"requires_reply": false}""", false)]
    [InlineData("""{"requires_reply": "false"}""", false)]
    [InlineData("""{"content": "hello"}""", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsRequiresReply_ParsesBooleanAndStringValues(string? toolArgs, bool expected)
    {
        OpenAiAdapter.IsRequiresReply(toolArgs).Should().Be(expected);
    }

    [Theory]
    [InlineData("""{"action": "set_checklist"}""", true)]
    [InlineData("""{"Action": "set_checklist"}""", true)]
    [InlineData("""{"operation": "SET_CHECKLIST"}""", true)]
    [InlineData("""{"command": "set_checklist"}""", true)]
    [InlineData("""{"action": "update_task_status"}""", false)]
    [InlineData("""{"action": "get_checklist"}""", false)]
    [InlineData("{}", false)]
    [InlineData(null, false)]
    public void IsSetChecklistAction_DetectsSetChecklistInVariousKeysAndCasings(string? toolArgs, bool expected)
    {
        OpenAiAdapter.IsSetChecklistAction(toolArgs).Should().Be(expected);
    }

    [Fact]
    public async Task ExecutePromptAsync_WhenProxyReturnsDirectJsonResponse_ParsesContentAndUsage()
    {
        var adapter = new OpenAiAdapter();
        var preset = new AgentPresetConfig
        {
            Id = "spence-ai",
            Name = "Spence AI",
            AdapterType = "openai",
            Endpoint = "http://127.0.0.1:11434/v1",
            DefaultModel = "qwen3.8-flash-next"
        };

        var promptEv = new PromptReceivedEvent
        {
            SessionId = Guid.NewGuid(),
            Prompt = "In agent-mesh is there an easy way to increase the size of the PVC?",
            Model = "qwen3.8-flash-next"
        };

        var directJson = """
        {
            "id": "chatcmpl-test",
            "choices": [
                {
                    "message": {
                        "role": "assistant",
                        "content": "Yes, you can configure personas.pvc.size in helm values."
                    }
                }
            ],
            "usage": {
                "prompt_tokens": 40,
                "completion_tokens": 15,
                "total_tokens": 55
            }
        }
        """;

        var mockClient = new HttpClient(new MockHttpMessageHandler(req =>
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(directJson, System.Text.Encoding.UTF8, "application/json")
            };
        }));

        OpenAiAdapter.SetHttpClientForTest(mockClient);
        try
        {
            var events = new List<TrajectoryEventPayload>();
            await adapter.ExecutePromptAsync(preset, promptEv, ev =>
            {
                events.Add(ev);
                return Task.CompletedTask;
            }, CancellationToken.None);

            events.Should().Contain(e => e.EventType == "response" && e.Content.Contains("Yes, you can configure personas.pvc.size in helm values."));
            events.Should().NotContain(e => e.EventType == "response" && e.Content == "Turn completed.");

            var resp = events.First(e => e.EventType == "response");
            resp.StructuredPayloadJson.Should().NotBeNull();
            resp.StructuredPayloadJson.Should().Contain("\"totalTokens\":55");
        }
        finally
        {
            OpenAiAdapter.ResetHttpClientForTest();
        }
    }

    [Fact]
    public async Task ExecutePromptAsync_SendsStreamOptionsWithIncludeUsage()
    {
        var adapter = new OpenAiAdapter();
        var preset = new AgentPresetConfig
        {
            Id = "spence-ai",
            Name = "Spence AI",
            AdapterType = "openai",
            Endpoint = "http://127.0.0.1:11434/v1",
            DefaultModel = "qwen3.8-flash-next"
        };

        var promptEv = new PromptReceivedEvent
        {
            SessionId = Guid.NewGuid(),
            Prompt = "Hello",
            Model = "qwen3.8-flash-next"
        };

        string? capturedBody = null;
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\ndata: [DONE]\n\n";

        var mockClient = new HttpClient(new MockHttpMessageHandler(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream")
            };
        }));

        OpenAiAdapter.SetHttpClientForTest(mockClient);
        try
        {
            await adapter.ExecutePromptAsync(preset, promptEv, _ => Task.CompletedTask, CancellationToken.None);

            capturedBody.Should().NotBeNull();
            using var doc = JsonDocument.Parse(capturedBody!);
            doc.RootElement.TryGetProperty("stream_options", out var streamOpts).Should().BeTrue();
            streamOpts.GetProperty("include_usage").GetBoolean().Should().BeTrue();
        }
        finally
        {
            OpenAiAdapter.ResetHttpClientForTest();
        }
    }

    [Fact]
    public async Task ExecutePromptAsync_WhenToolBatchHasSetChecklistAndPostJobMessage_ExecutesBothAndParksTurn()
    {
        var executedTools = new List<string>();
        OpenAiAdapter.PlatformToolExecutor = (toolName, args, sId, pId, aId, ct) =>
        {
            executedTools.Add(toolName);
            return Task.FromResult("{\"success\":true}");
        };

        var adapter = new OpenAiAdapter();
        var preset = new AgentPresetConfig
        {
            Id = "test-agent",
            Name = "Test Agent",
            AdapterType = "openai",
            Endpoint = "http://127.0.0.1:11434/v1",
            DefaultModel = "test-model"
        };

        var sessionId = Guid.NewGuid();
        var promptEv = new PromptReceivedEvent
        {
            SessionId = sessionId,
            Prompt = "Run discovery",
            Model = "test-model"
        };

        var directJson = """
        {
            "id": "chatcmpl-test",
            "choices": [
                {
                    "message": {
                        "role": "assistant",
                        "content": null,
                        "tool_calls": [
                            {
                                "id": "call_1",
                                "type": "function",
                                "function": {
                                    "name": "manage_task",
                                    "arguments": "{\"action\":\"set_checklist\",\"tasks\":[{\"id\":\"t1\",\"title\":\"Task 1\"}]}"
                                }
                            },
                            {
                                "id": "call_2",
                                "type": "function",
                                "function": {
                                    "name": "post_job_message",
                                    "arguments": "{\"message\":\"Clarification question: table vs metadata?\",\"requires_reply\":true}"
                                }
                            }
                        ]
                    }
                }
            ]
        }
        """;

        var mockClient = new HttpClient(new MockHttpMessageHandler(_ =>
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(directJson, System.Text.Encoding.UTF8, "application/json")
            };
        }));

        OpenAiAdapter.SetHttpClientForTest(mockClient);
        try
        {
            var events = new List<TrajectoryEventPayload>();
            await adapter.ExecutePromptAsync(preset, promptEv, ev =>
            {
                events.Add(ev);
                return Task.CompletedTask;
            }, CancellationToken.None);

            executedTools.Should().Contain("manage_task");
            executedTools.Should().Contain("post_job_message");
            events.Should().Contain(e => e.EventType == "response" && e.Content.Contains("Turn paused: plan / question posted to Job Chat awaiting reply."));

            var history = OpenAiAdapter.GetSessionHistory(sessionId)!;
            history.Should().Contain(m => m.Role == "assistant" && m.ToolCalls != null && m.ToolCalls.Count == 2);
            history.Should().Contain(m => m.Role == "tool" && m.ToolCallId == "call_1");
            history.Should().Contain(m => m.Role == "tool" && m.ToolCallId == "call_2");
        }
        finally
        {
            OpenAiAdapter.ResetHttpClientForTest();
            OpenAiAdapter.PlatformToolExecutor = null;
        }
    }

    [Fact]
    public async Task ExecutePromptAsync_WhenToolBatchHasOnlySetChecklist_ConcludesTurnAwaitingPlanApproval()
    {
        var executedTools = new List<string>();
        OpenAiAdapter.PlatformToolExecutor = (toolName, args, sId, pId, aId, ct) =>
        {
            executedTools.Add(toolName);
            return Task.FromResult("{\"success\":true}");
        };

        var adapter = new OpenAiAdapter();
        var preset = new AgentPresetConfig
        {
            Id = "test-agent",
            Name = "Test Agent",
            AdapterType = "openai",
            Endpoint = "http://127.0.0.1:11434/v1",
            DefaultModel = "test-model"
        };

        var sessionId = Guid.NewGuid();
        var promptEv = new PromptReceivedEvent
        {
            SessionId = sessionId,
            Prompt = "Run discovery",
            Model = "test-model"
        };

        var directJson = """
        {
            "id": "chatcmpl-test",
            "choices": [
                {
                    "message": {
                        "role": "assistant",
                        "content": null,
                        "tool_calls": [
                            {
                                "id": "call_1",
                                "type": "function",
                                "function": {
                                    "name": "manage_task",
                                    "arguments": "{\"action\":\"set_checklist\",\"tasks\":[{\"id\":\"t1\",\"title\":\"Task 1\"}]}"
                                }
                            }
                        ]
                    }
                }
            ]
        }
        """;

        var mockClient = new HttpClient(new MockHttpMessageHandler(_ =>
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(directJson, System.Text.Encoding.UTF8, "application/json")
            };
        }));

        OpenAiAdapter.SetHttpClientForTest(mockClient);
        try
        {
            var events = new List<TrajectoryEventPayload>();
            await adapter.ExecutePromptAsync(preset, promptEv, ev =>
            {
                events.Add(ev);
                return Task.CompletedTask;
            }, CancellationToken.None);

            executedTools.Should().ContainSingle().Which.Should().Be("manage_task");
            events.Should().Contain(e => e.EventType == "response" && e.Content.Contains("Turn concluded: Living task checklist registered. Awaiting Stage 2 Plan Approval."));

            var history = OpenAiAdapter.GetSessionHistory(sessionId)!;
            history.Should().Contain(m => m.Role == "assistant" && m.ToolCalls != null && m.ToolCalls.Count == 1);
            history.Should().Contain(m => m.Role == "tool" && m.ToolCallId == "call_1");
        }
        finally
        {
            OpenAiAdapter.ResetHttpClientForTest();
            OpenAiAdapter.PlatformToolExecutor = null;
        }
    }

    [Fact]
    public void ResolveModelContextLimit_RespectsConfigured512kProfileWithoutClampingTo128k()
    {
        var presetWithTokens = new AgentPresetConfig
        {
            Id = "spence-ai",
            AdapterType = "openai",
            DefaultModel = "llama3.3",
            ContextWindowTokens = 524_288
        };

        var presetWithSettings = new AgentPresetConfig
        {
            Id = "spence-ai",
            AdapterType = "openai",
            DefaultModel = "deepseek-r1",
            OpenAi = new OpenAISettings { DefaultContextSize = "512k" }
        };

        OpenAiAdapter.ResolveModelContextLimit("llama3.3", null, presetWithTokens).Should().Be(524_288);
        OpenAiAdapter.ResolveModelContextLimit("deepseek-r1", "default", presetWithSettings).Should().Be(524_288);
        OpenAiAdapter.ResolveModelContextLimit("qwen2.5-coder", "524288", presetWithTokens).Should().Be(524_288);
    }

    [Fact]
    public async Task ExecutePromptAsync_TriggersCompactionAt75PercentOfContextWindow_WithoutCappingThinkingTokens()
    {
        var sessionId = Guid.NewGuid();
        var adapter = new OpenAiAdapter();
        var preset = new AgentPresetConfig
        {
            Id = "spence-ai",
            Name = "Spence AI",
            AdapterType = "openai",
            Endpoint = "http://127.0.0.1:11434/v1",
            DefaultModel = "qwen3.8-flash-next",
            ContextWindowTokens = 4_000,
            CompactionThresholdPercent = 75
        };

        OpenAiAdapter.ResolveCompactionThresholdPercent(preset).Should().Be(75);

        // Pre-seed >3,000 estimated tokens (>75% of 4,000, <80% of 4,000 = 3,200) so 75% triggers compaction
        // whereas the old 80% threshold would NOT have triggered!
        var seeded = new List<OpenAiMessage>
        {
            new() { Role = "system", Content = "You are Rook." },
            new() { Role = "user", Content = "[TASK ENVELOPE & WORKING ENVIRONMENT]\nTask: implement compaction" },
            new() { Role = "assistant", Content = "Reading file", ToolCalls = [new OpenAiToolCall { Id = "c1", Function = new OpenAiFunctionCall { Name = "read_file", Arguments = "{}" } }] },
            new() { Role = "tool", ToolCallId = "c1", Content = new string('x', 10_900) },
            new() { Role = "user", Content = "Continue1" },
            new() { Role = "assistant", Content = "Ack1" },
            new() { Role = "user", Content = "Continue2" },
            new() { Role = "assistant", Content = "Ack2" }
        };
        OpenAiAdapter.SaveHistoryToDisk(sessionId, seeded);

        string? mainRequestBody = null;
        var mockClient = new HttpClient(new MockHttpMessageHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains("\"stream\":false"))
            {
                // Summarizer request fails gracefully -> CompactHistory truncates tool output
                return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);
            }

            mainRequestBody = body;
            var sse = "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Deep unbounded reasoning...\",\"content\":\"Done.\"}}]}\n\ndata: [DONE]\n\n";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream")
            };
        }));

        OpenAiAdapter.SetHttpClientForTest(mockClient);
        try
        {
            var events = new List<TrajectoryEventPayload>();
            await adapter.ExecutePromptAsync(
                preset,
                new PromptReceivedEvent { SessionId = sessionId, Prompt = "Proceed", Model = "qwen3.8-flash-next" },
                ev => { events.Add(ev); return Task.CompletedTask; },
                CancellationToken.None);

            events.Should().Contain(e => e.EventType == "thought" && e.Content.Contains("threshold 75%"));
            mainRequestBody.Should().NotBeNull();
            mainRequestBody.Should().NotContain("\"max_tokens\"", "main agent turn must never cap reasoning/thinking tokens");
            mainRequestBody.Should().NotContain("\"max_completion_tokens\"");
        }
        finally
        {
            OpenAiAdapter.ResetHttpClientForTest();
            OpenAiAdapter.ClearSessionHistory(sessionId);
        }
    }

    [Fact]
    public async Task ExecutePromptAsync_EnforcesPostFirstByteChunkIdleWatchdog_AndRetriesStalledStream()
    {
        var sessionId = Guid.NewGuid();
        var adapter = new OpenAiAdapter();
        var preset = new AgentPresetConfig
        {
            Id = "spence-ai",
            Name = "Spence AI",
            AdapterType = "openai",
            Endpoint = "http://127.0.0.1:11434/v1",
            DefaultModel = "qwen3.8-flash-next",
            TimeoutSeconds = 30,
            StreamChunkIdleTimeoutSeconds = 1,
            MaxRetries = 1,
            RetryDelayMilliseconds = 10
        };

        var attempt = 0;
        var mockClient = new HttpClient(new AsyncStreamingHttpMessageHandler(async (_, ct) =>
        {
            var currentAttempt = Interlocked.Increment(ref attempt);
            if (currentAttempt == 1)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StallingSseContent("data: {\"choices\":[{\"delta\":{\"content\":\"First chunk\"}}]}\n\n", TimeSpan.FromSeconds(5))
                };
            }

            await Task.Yield();
            var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"Recovered response after retry.\"}}]}\n\ndata: [DONE]\n\n";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(sse, System.Text.Encoding.UTF8, "text/event-stream")
            };
        }));

        OpenAiAdapter.SetHttpClientForTest(mockClient);
        try
        {
            var events = new List<TrajectoryEventPayload>();
            await adapter.ExecutePromptAsync(
                preset,
                new PromptReceivedEvent { SessionId = sessionId, Prompt = "Test watchdog", Model = "qwen3.8-flash-next" },
                ev => { events.Add(ev); return Task.CompletedTask; },
                CancellationToken.None);

            attempt.Should().Be(2, "stalled stream after first SSE chunk must cancel and retry");
            events.Should().Contain(e => e.EventType == "thought" && e.Content.Contains("SSE stream stalled"));
            events.Should().Contain(e => e.EventType == "response" && e.Content.Contains("Recovered response after retry."));
        }
        finally
        {
            OpenAiAdapter.ResetHttpClientForTest();
            OpenAiAdapter.ClearSessionHistory(sessionId);
        }
    }

    private sealed class AsyncStreamingHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

    private sealed class StallingSseContent(string firstChunk, TimeSpan stallDuration) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(firstChunk);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
            await Task.Delay(stallDuration);
        }

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<Stream>(new StallingReadStream(firstChunk, stallDuration));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    private sealed class StallingReadStream(string firstChunk, TimeSpan stallDuration) : Stream
    {
        private readonly byte[] _firstBytes = System.Text.Encoding.UTF8.GetBytes(firstChunk);
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < _firstBytes.Length)
            {
                var toCopy = Math.Min(buffer.Length, _firstBytes.Length - _offset);
                _firstBytes.AsMemory(_offset, toCopy).CopyTo(buffer);
                _offset += toCopy;
                return toCopy;
            }

            await Task.Delay(stallDuration, cancellationToken);
            return 0;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
