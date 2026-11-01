using System.Text.Json;
using AgentMesh.Cli.Adapters;
using AgentMesh.Cli.Models;
using AgentMesh.Core.Services;
using FluentAssertions;
using Xunit;

namespace AgentMesh.Tests.Cli;

/// <summary>
/// Message ordering at the dispatch boundary: what the model is actually sent, and in what order.
/// </summary>
/// <remarks>
/// <para>
/// These assert against the serialised request body rather than against internal state, because the
/// internal list is not what the model reads. A guard that passes on the in-memory list while the
/// payload still ends on a system message would protect nothing.
/// </para>
/// <para>
/// Every test isolates <see cref="OpenAiAdapter.HistoryDirectoryPath"/> into a fresh temp directory.
/// The adapter loads history from disk when its in-memory cache misses, so without isolation a
/// developer's real <c>~/.agentmesh/history</c> would seed the conversation and the assertions about
/// <c>messages[0]</c> and <c>messages[1]</c> would depend on whatever that machine last ran.
/// </para>
/// </remarks>
[Collection("OpenAiAdapterStaticSeams")]
public class OpenAiAdapterMessageOrderingTests : IDisposable
{
    private readonly string _historyDir =
        Path.Combine(Path.GetTempPath(), $"agentmesh-ordering-{Guid.NewGuid():N}");

    /// <summary>
    /// Sessions this test created, cleared on dispose.
    /// </summary>
    /// <remarks>
    /// The adapter keeps a process-wide session cache. Each test uses a fresh Guid so runs cannot
    /// collide, but the cache would still grow across a suite and a leaked entry would outlive the temp
    /// directory it was written under.
    /// </remarks>
    private readonly List<Guid> _sessions = [];

    public OpenAiAdapterMessageOrderingTests() => Directory.CreateDirectory(_historyDir);

    public void Dispose()
    {
        OpenAiAdapter.ResetHttpClientForTest();
        foreach (var id in _sessions) OpenAiAdapter.ClearSessionHistory(id);
        try { Directory.Delete(_historyDir, recursive: true); } catch (IOException) { }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // The spec's named test
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The payload handed to the model never ends on a system message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The idle-loop defect this job fixes: an instruction-tuned model that reads a system message last
    /// concludes that environment setup has <em>just</em> completed and that nobody has asked it
    /// anything, so it replies that it is waiting for instructions. The turn burns itself doing nothing
    /// and the transcript looks clean, which is why it needed a guard at the wire rather than a rule in
    /// a prompt.
    /// </para>
    /// <para>
    /// Asserted on the captured HTTP body, not on the adapter's list: the list is repaired in several
    /// places and the body is the one thing the model is guaranteed to have seen.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task OpenAiAdapter_NeverEmitsTrailingSystemMessage()
    {
        var captured = await RunAsync(new PromptStackWirePayload(
            "[LAYER 1: PLATFORM INVARIANTS] the cached prefix",
            PromptStackWirePayload.EnvelopeMarker + "\n* Task ID: #9\n\n[OBJECTIVE]\nShip the guard.",
            [
                new TurnHistoryMessage("assistant", "I ran the tests and they failed."),
                new TurnHistoryMessage("user", "Try the scoped filter instead."),
            ]));

        var roles = Roles(captured);

        roles.Should().NotBeEmpty();
        roles[^1].Should().NotBe("system",
            "a trailing system message makes the model wait for a user prompt instead of driving the task");
        roles[^1].Should().BeOneOf("user", "tool");
    }

    /// <summary>
    /// A stray system message appended mid-session is relocated out of the tail before dispatch.
    /// </summary>
    /// <remarks>
    /// The realistic shape of the defect: a harness adapter that appends a status notice after the tool
    /// round-trips. The guard has to hold for a session that was already polluted, not only for a clean
    /// one, because the pollution is exactly what the reported bug looked like.
    /// </remarks>
    [Fact]
    public async Task OpenAiAdapter_RelocatesAStraySystemMessageOutOfTheTail()
    {
        var sessionId = Guid.NewGuid();

        // Seed the on-disk history the way a buggy producer would leave it: prefix, envelope, a turn,
        // then a system notice appended at the end.
        var polluted = new List<OpenAiMessage>
        {
            new() { Role = "system", Content = "[LAYER 1: PLATFORM INVARIANTS] prefix" },
            new() { Role = "user", Content = PromptStackWirePayload.EnvelopeMarker + "\n* Task ID: #9" },
            new() { Role = "assistant", Content = "Ran the build." },
            new() { Role = "system", Content = "[harness] session resumed, context rehydrated" },
        };
        WriteHistory(sessionId, polluted);

        var captured = await RunAsync(promptStack: null, sessionId: sessionId);

        var roles = Roles(captured);

        roles[^1].Should().NotBe("system");
        roles.Should().NotContain(r => r == "system" && roles.IndexOf(r) != 0,
            "there is exactly one system message and it leads the array");

        // The instruction survives the relocation rather than being dropped to satisfy the rule.
        captured.Should().Contain("session resumed, context rehydrated");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // The split: messages[0] and messages[1]
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The cached prefix leads and the task envelope is the first user turn.
    /// </summary>
    /// <remarks>
    /// The payload contract from the spec, asserted on the wire. Layers 1–5 form the stable prefix an
    /// inference engine reuses across turns; the envelope is the volatile half and must sit after it, as
    /// a user turn, never appended to the system block.
    /// </remarks>
    [Fact]
    public async Task OpenAiAdapter_SendsCachedPrefixThenTaskEnvelopeAsTheUserTurn()
    {
        const string prefix = "[LAYER 1: PLATFORM INVARIANTS] platform rules\n\n---\n\n[LAYER 2: MASTER PERSONA] Rook";
        var envelope = PromptStackWirePayload.EnvelopeMarker + "\n* Task ID: #42\n\n[OBJECTIVE]\nImplement the split.";

        var captured = await RunAsync(new PromptStackWirePayload(prefix, envelope));

        var messages = Messages(captured);

        messages[0].Role.Should().Be("system");
        messages[0].Content.Should().Be(prefix,
            "the prefix is the cache key, so it must reach the model byte-for-byte and lead the array");

        messages[1].Role.Should().Be("user");
        messages[1].Content.Should().StartWith(PromptStackWirePayload.EnvelopeMarker);
        messages[1].Content.Should().Contain("Implement the split.");
    }

    /// <summary>
    /// The flattened stack is not resent inside the user turn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The regression this guards is invisible to a "does the prompt contain the persona?" test, because
    /// it does — twice. <c>PromptReceivedEvent.Prompt</c> carries the <em>flattened</em> stack (layers
    /// 1–6) for backward compatibility with pre-split binaries and for the session row. Using it as the
    /// user turn after sending layers 1–5 as the system message would state the platform rules, the
    /// persona, the project manifest, the roster and the memories a second time, immediately after the
    /// model was told them, in the position it weights most.
    /// </para>
    /// <para>
    /// That is the same token burn this job removes from the system block, re-imported through the user
    /// turn — and it would pass every other assertion in this file.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task OpenAiAdapter_DoesNotResendTheCachedLayersInsideTheUserTurn()
    {
        const string prefix = "[LAYER 1: PLATFORM INVARIANTS] platform rules";
        var envelope = PromptStackWirePayload.EnvelopeMarker + "\n* Task ID: #42";

        var captured = await RunAsync(
            new PromptStackWirePayload(prefix, envelope),
            // The dispatcher also sends the flattened form; the adapter must prefer the split.
            flattenedPrompt: prefix + "\n\n---\n\n" + envelope);

        var userTurns = Messages(captured).Where(m => m.Role == "user").ToList();

        userTurns.Should().HaveCountGreaterThanOrEqualTo(1);
        foreach (var turn in userTurns)
        {
            turn.Content.Should().NotContain("[LAYER 1: PLATFORM INVARIANTS]",
                "layers 1-5 travel as the system message; repeating them in the user turn doubles the " +
                "cost of the prefix and duplicates the instruction the model is most likely to obey twice");
        }

        // And the prefix is present exactly once, in exactly one place.
        CountOccurrences(captured, "[LAYER 1: PLATFORM INVARIANTS]").Should().Be(1);
    }

    /// <summary>
    /// The sliding window is replayed as real turns, between the prefix and the envelope.
    /// </summary>
    /// <remarks>
    /// Not an optimisation but the only continuity a macro turn has: every macro turn mints a brand-new
    /// turn session, so the adapter's own history is empty at the start of turn 2, 3 and 4. Without the
    /// seed the agent is handed its opening context every time and never its recent work.
    /// </remarks>
    [Fact]
    public async Task OpenAiAdapter_SeedsTheSlidingWindowAsPriorTurns()
    {
        var captured = await RunAsync(new PromptStackWirePayload(
            "[LAYER 1: PLATFORM INVARIANTS] prefix",
            PromptStackWirePayload.EnvelopeMarker + "\n* Task ID: #7",
            [
                new TurnHistoryMessage("assistant", "STEP-ONE fixed the reconciler"),
                new TurnHistoryMessage("user", "now cover the checkpoint writer"),
                new TurnHistoryMessage("assistant", "STEP-TWO wrote the checkpoint"),
            ]));

        var messages = Messages(captured);

        messages[0].Role.Should().Be("system");

        // The window sits after the prefix. Where the envelope lands relative to it is asserted below
        // by content, not by index, because the ordering that matters is "the model reads the objective
        // last", and a summary pinned between the prefix and the window would shift every index.
        var transcript = messages.Skip(1).Select(m => m.Content).ToList();
        var first = FindIndex(messages, "STEP-ONE");
        var second = FindIndex(messages, "now cover the checkpoint");
        var third = FindIndex(messages, "STEP-TWO");

        first.Should().BeGreaterThan(0, "prior turns must be replayed, not dropped");
        new[] { first, second, third }.Should().BeInAscendingOrder(
            "a reversed transcript reads as the agent re-answering its own last question");

        messages[first].Role.Should().Be("assistant");
        messages[second].Role.Should().Be("user");
    }

    /// <summary>
    /// The objective is the last thing the model reads.
    /// </summary>
    /// <remarks>
    /// Recency is what an instruction-tuned model weights most, and it is the reason the envelope is a
    /// user turn rather than a system block: the same text in the other position produces "understood, I
    /// will wait" instead of work.
    /// </remarks>
    [Fact]
    public async Task OpenAiAdapter_EndsOnTheTaskEnvelope()
    {
        var captured = await RunAsync(new PromptStackWirePayload(
            "[LAYER 1: PLATFORM INVARIANTS] prefix",
            PromptStackWirePayload.EnvelopeMarker + "\n* Task ID: #7\n\n[OBJECTIVE]\nClose the loop.",
            [new TurnHistoryMessage("assistant", "an earlier turn")]));

        var messages = Messages(captured);

        messages[^1].Role.Should().Be("user");
        messages[^1].Content.Should().Contain(PromptStackWirePayload.EnvelopeMarker)
            .And.Contain("Close the loop.");
    }

    /// <summary>
    /// An orphan <c>tool</c> role never reaches the wire.
    /// </summary>
    /// <remarks>
    /// A <c>role: "tool"</c> message is only valid when it answers a preceding assistant
    /// <c>tool_calls</c> entry by id, and the harness stores no <c>tool_call_id</c> on a session row. A
    /// replayed tool result is therefore an orphan, and OpenAI-compatible servers reject the pairing
    /// with a 400 — a hard failure on every turn of a session that has one.
    /// </remarks>
    [Fact]
    public async Task OpenAiAdapter_NeverSendsAnOrphanToolMessage()
    {
        var captured = await RunAsync(new PromptStackWirePayload(
            "[LAYER 1: PLATFORM INVARIANTS] prefix",
            PromptStackWirePayload.EnvelopeMarker + "\n* Task ID: #7",
            [
                new TurnHistoryMessage("tool", "42 tests passed", "tool_result"),
                new TurnHistoryMessage("assistant", "the tests are green"),
            ]));

        var messages = Messages(captured);

        messages.Should().NotContain(m => m.Role == "tool",
            "a tool message without a matching tool_call id is a 400 from the server");
        messages.Should().Contain(m => m.Content == "42 tests passed",
            "the information is kept; only the protocol-invalid role is corrected");
    }

    /// <summary>
    /// A window row never occupies the system slot, whatever role it carries.
    /// </summary>
    /// <remarks>
    /// The ordering rule is "system first, envelope second", which means exactly one system message and
    /// it leads the array. The composer's own mapper only ever emits <c>user</c> or <c>assistant</c>, but
    /// the window may have been written by a different build, and a leaked system row landing mid-array
    /// would put a second instruction block between the prefix and the objective. Coercing to
    /// <c>user</c> keeps the payload legal and keeps the text.
    /// </remarks>
    [Fact]
    public async Task OpenAiAdapter_KeepsExactlyOneSystemMessageWhenTheWindowLeaksOne()
    {
        var captured = await RunAsync(new PromptStackWirePayload(
            "[LAYER 1: PLATFORM INVARIANTS] prefix",
            PromptStackWirePayload.EnvelopeMarker + "\n* Task ID: #7",
            [
                new TurnHistoryMessage("system", "LEAKED-HARNESS-NOTICE"),
                new TurnHistoryMessage("assistant", "a normal turn"),
            ]));

        var messages = Messages(captured);

        messages.Count(m => m.Role == "system").Should().Be(1,
            "several system blocks are ambiguous, and some servers honour only the first");
        messages[0].Content.Should().Be("[LAYER 1: PLATFORM INVARIANTS] prefix");
        messages.Should().Contain(m => m.Content == "LEAKED-HARNESS-NOTICE",
            "the text is relocated, never deleted");
        FindIndex(messages, "LEAKED-HARNESS-NOTICE").Should().BeGreaterThan(0);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Fallback: a dispatch with no stack
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A dispatch from an API that predates the split still produces a usable payload.
    /// </summary>
    /// <remarks>
    /// This is what makes the rollout non-lockstep. The CLI binary is published to <c>dist/cli/</c> and
    /// baked into persona container images rebuilt on their own schedule, so a new worker against an old
    /// CLI, and a new CLI against an old API, are both real states rather than hypotheticals. Neither
    /// may produce a turn with no system message or no prompt.
    /// </remarks>
    [Fact]
    public async Task OpenAiAdapter_WithoutAStackFallsBackToTheLegacySinglePrompt()
    {
        var captured = await RunAsync(
            promptStack: null,
            flattenedPrompt: "[Project Context: Project \"Agent Mesh\"]\nDo the legacy thing.");

        var messages = Messages(captured);

        messages[0].Role.Should().Be("system",
            "the adapter still supplies its own system prompt when the dispatch carries none");
        messages.Should().Contain(m => m.Role == "user" && m.Content!.Contains("Do the legacy thing."),
            "the prompt must not be dropped just because it did not arrive pre-split");
        Roles(captured)[^1].Should().NotBe("system");
    }

    /// <summary>
    /// A half-populated stack is treated as absent rather than half-applied.
    /// </summary>
    [Fact]
    public async Task OpenAiAdapter_TreatsAnIncompleteStackAsNoStack()
    {
        var captured = await RunAsync(
            new PromptStackWirePayload("   ", PromptStackWirePayload.EnvelopeMarker + "\n* Task ID: #7"),
            flattenedPrompt: "the real prompt");

        var messages = Messages(captured);

        messages[0].Role.Should().Be("system");
        messages[0].Content.Should().NotBe("   ");
        messages.Should().Contain(m => m.Role == "user" && m.Content!.Contains("the real prompt"),
            "an envelope with no prefix is worse than no envelope at all: the agent gets a task with no " +
            "persona, no conventions and no memories");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Compaction must not eat the envelope
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Compaction leaves the task envelope intact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hazard the split introduced. <c>CompactHistory</c> truncates and then deletes messages from
    /// index 1 — which, before the split, was always a prunable conversation turn, and which is now the
    /// envelope. On a long turn, the turns where compaction actually fires, the task id, the acceptance
    /// criteria, the working root and the git recovery note would be the first casualty, and the agent
    /// would end up mid-session with no task. That is the same idle loop this job exists to eliminate,
    /// arriving later in the session with a cleaner-looking transcript.
    /// </para>
    /// <para>
    /// Asserted directly on the compaction pass rather than through a full turn, because reaching it
    /// through the wire would require a session long enough to cross the token threshold — a fixture
    /// that is slow, brittle, and sensitive to the tokenizer.
    /// </para>
    /// </remarks>
    [Fact]
    public void CompactHistory_NeverPrunesTheTaskEnvelope()
    {
        var history = new List<OpenAiMessage>
        {
            new() { Role = "system", Content = "[LAYER 1: PLATFORM INVARIANTS] " + new string('p', 8_000) },
            new() { Role = "user", Content = PromptStackWirePayload.EnvelopeMarker + "\n* Task ID: #9\n[OBJECTIVE]\nFinish it." },
        };
        history.AddRange(Enumerable.Range(0, 12).Select(i => new OpenAiMessage
        {
            Role = i % 2 == 0 ? "assistant" : "user",
            Content = new string('x', 3_000) + $" turn-{i}",
        }));

        var compacted = OpenAiAdapter.CompactHistory(history, targetMaxTokens: 1_000, forceAggressive: true);

        compacted.Should().BeTrue("the fixture is far over budget, so compaction must report work done");

        var envelope = history.FirstOrDefault(m => m.Role == "user" &&
                                                  m.Content != null &&
                                                  m.Content.StartsWith(PromptStackWirePayload.EnvelopeMarker));

        envelope.Should().NotBeNull("the envelope must survive compaction");
        envelope!.Content.Should().Contain("Task ID: #9")
            .And.Contain("Finish it.",
                "a truncated envelope is a task the agent can see the shape of and cannot act on");
    }

    /// <summary>
    /// The pinned-summary slot is recognised, so compaction does not fold it into its own input.
    /// </summary>
    [Fact]
    public void FirstPrunableIndex_SkipsThePrefixEnvelopeAndSummary()
    {
        var history = new List<OpenAiMessage>
        {
            new() { Role = "system", Content = "prefix" },
            new() { Role = "user", Content = PromptStackWirePayload.EnvelopeMarker + " envelope" },
            new() { Role = "system", Content = OpenAiAdapter.SummaryMarker + " earlier turns]" },
            new() { Role = "assistant", Content = "first prunable" },
        };

        OpenAiAdapter.FirstPrunableIndex(history).Should().Be(3);
    }

    [Fact]
    public void FirstPrunableIndex_WithALegacyLayoutStartsAtOne()
    {
        var history = new List<OpenAiMessage>
        {
            new() { Role = "system", Content = "prefix" },
            new() { Role = "user", Content = "an ordinary first turn" },
        };

        OpenAiAdapter.FirstPrunableIndex(history).Should().Be(1,
            "a dispatch with no envelope keeps the pre-split behaviour exactly");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // The guard, in isolation
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SanitizeMessageOrder_MergesTrailingSystemMessagesIntoThePrefix()
    {
        var history = new List<OpenAiMessage>
        {
            new() { Role = "system", Content = "prefix" },
            new() { Role = "user", Content = "envelope" },
            new() { Role = "assistant", Content = "a turn" },
            new() { Role = "system", Content = "stray one" },
            new() { Role = "system", Content = "stray two" },
        };

        var relocated = OpenAiAdapter.SanitizeMessageOrder(history);

        relocated.Should().Be(2, "both strays move, not just the last one");
        history[^1].Role.Should().Be("assistant");
        history.Count(m => m.Role == "system").Should().Be(1,
            "one system message, leading the array — several would each be a separate instruction block " +
            "and some servers only honour the first");
        history[0].Content.Should().Contain("prefix").And.Contain("stray one").And.Contain("stray two",
            "the instruction is preserved, only its position was wrong");
    }

    [Fact]
    public void SanitizeMessageOrder_LeavesAWellFormedPayloadAlone()
    {
        var history = new List<OpenAiMessage>
        {
            new() { Role = "system", Content = "prefix" },
            new() { Role = "user", Content = "envelope" },
        };

        OpenAiAdapter.SanitizeMessageOrder(history).Should().Be(0);
        history[0].Content.Should().Be("prefix", "the common path must not rewrite the cached prefix");
    }

    [Fact]
    public void SanitizeMessageOrder_PromotesTheSystemMessageWhenNothingLeads()
    {
        var history = new List<OpenAiMessage>
        {
            new() { Role = "user", Content = "envelope" },
            new() { Role = "system", Content = "orphaned instruction" },
        };

        OpenAiAdapter.SanitizeMessageOrder(history).Should().Be(1);
        history[0].Role.Should().Be("system");
        history[^1].Role.Should().Be("user");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs one turn against a stubbed endpoint and returns the captured request body.
    /// </summary>
    private async Task<string> RunAsync(
        PromptStackWirePayload? promptStack,
        string? flattenedPrompt = null,
        Guid? sessionId = null)
    {
        OpenAiAdapter.HistoryDirectoryPath = _historyDir;

        var sid = sessionId ?? Guid.NewGuid();
        _sessions.Add(sid);
        var preset = new AgentPresetConfig
        {
            Id = "order-test",
            Name = "Ordering Test",
            AdapterType = "openai",
            Endpoint = "http://127.0.0.1:1/v1",
            MaxSteps = 1,
        };

        var promptEvent = new PromptReceivedEvent
        {
            SessionId = sid,
            MessageId = Guid.NewGuid(),
            AgentId = "order-test",
            Prompt = flattenedPrompt ?? promptStack?.UserTaskEnvelope ?? "plain prompt",
            PromptStack = promptStack,
            WorkingDirectory = _historyDir,
            Sender = "operator",
        };

        string? capturedBody = null;
        using var client = new HttpClient(new StubHandler(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("chat/completions"))
            {
                capturedBody = ReadBody(req);
                // A plain text answer with no tool calls, so the turn ends after one request.
                return Sse("{\"choices\":[{\"delta\":{\"content\":\"Done.\"}}]}\n\ndata: [DONE]\n\n");
            }

            capturedBody ??= ReadBody(req);
            return Sse("{\"choices\":[{\"delta\":{\"content\":\"summary\"}}]}\n\ndata: [DONE]\n\n");
        }));

        OpenAiAdapter.SetHttpClientForTest(client);

        var adapter = new OpenAiAdapter();
        await adapter.ExecutePromptAsync(preset, promptEvent, _ => Task.CompletedTask, CancellationToken.None);

        capturedBody.Should().NotBeNull("the adapter must have attempted a request");
        return capturedBody!;
    }

    private static string ReadBody(HttpRequestMessage req) =>
        req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;

    private static HttpResponseMessage Sse(string payload) =>
        new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "text/event-stream"),
        };

    private record WireMessage(string Role, string? Content);

    private static List<WireMessage> Messages(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => new WireMessage(
                m.GetProperty("role").GetString() ?? string.Empty,
                m.TryGetProperty("content", out var c)
                    ? (c.ValueKind == JsonValueKind.String ? c.GetString() : c.ToString())
                    : null))
            .ToList();
    }

    private static List<string> Roles(string body) => Messages(body).Select(m => m.Role).ToList();

    private static int FindIndex(List<WireMessage> messages, string needle)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Content?.Contains(needle, StringComparison.Ordinal) == true) return i;
        }

        return -1;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>Writes the on-disk history file the adapter loads when its cache misses.</summary>
    private void WriteHistory(Guid sessionId, List<OpenAiMessage> messages)
    {
        var path = Path.Combine(_historyDir, $"{sessionId}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(messages));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
