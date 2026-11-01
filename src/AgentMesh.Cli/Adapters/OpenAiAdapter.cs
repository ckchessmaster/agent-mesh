using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentMesh.Cli.Models;
using AgentMesh.Cli.Services;
using AgentMesh.Core.Services;

namespace AgentMesh.Cli.Adapters;

public class OpenAiAdapter : IAgentAdapter, IDescribesOwnCapabilities
{
    public string AdapterType => "openai";

    private static readonly ConcurrentDictionary<Guid, List<OpenAiMessage>> SessionHistories = new();
    private static readonly HttpClient DefaultHttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static HttpClient HttpClientInstance = DefaultHttpClient;

    public static void SetHttpClientForTest(HttpClient client)
    {
        HttpClientInstance = client;
    }

    public static void ResetHttpClientForTest()
    {
        HttpClientInstance = DefaultHttpClient;
    }

    public static Func<Guid, string, string, Task>? CommandOutputBroadcaster { get; set; }

    /// <summary>
    /// Optional callback invoked when a context-window overflow error reveals a model's true token limit.
    /// Args: (driverId, modelId, maxContextTokens).
    /// </summary>
    public static Func<string, string, int, Task>? LearnedContextLimitReporter { get; set; }

    public static string HistoryDirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".agentmesh",
        "openai_histories");
    private static readonly object HistoryDiskLock = new();

    public static void SaveHistoryToDisk(Guid sessionId, List<OpenAiMessage> history)
    {
        try
        {
            lock (HistoryDiskLock)
            {
                if (!Directory.Exists(HistoryDirectoryPath)) Directory.CreateDirectory(HistoryDirectoryPath);
                var filePath = Path.Combine(HistoryDirectoryPath, $"{sessionId}.json");
                var json = JsonSerializer.Serialize(history);
                File.WriteAllText(filePath, json);
            }
        }
        catch { }
    }

    public static List<OpenAiMessage>? LoadHistoryFromDisk(Guid sessionId)
    {
        try
        {
            lock (HistoryDiskLock)
            {
                var filePath = Path.Combine(HistoryDirectoryPath, $"{sessionId}.json");
                if (File.Exists(filePath))
                {
                    var json = File.ReadAllText(filePath);
                    return JsonSerializer.Deserialize<List<OpenAiMessage>>(json);
                }
            }
        }
        catch { }
        return null;
    }

    public static void DeleteHistoryFromDisk(Guid sessionId)
    {
        try
        {
            lock (HistoryDiskLock)
            {
                var filePath = Path.Combine(HistoryDirectoryPath, $"{sessionId}.json");
                if (File.Exists(filePath)) File.Delete(filePath);
            }
        }
        catch { }
    }

    public static void ClearSessionHistory(Guid sessionId)
    {
        SessionHistories.TryRemove(sessionId, out _);
        DeleteHistoryFromDisk(sessionId);
    }

    public static List<OpenAiMessage>? GetSessionHistory(Guid sessionId)
    {
        if (SessionHistories.TryGetValue(sessionId, out var history))
            return history;

        var loaded = LoadHistoryFromDisk(sessionId);
        if (loaded != null)
        {
            SessionHistories[sessionId] = loaded;
            return loaded;
        }

        return null;
    }

    public static int ResolveTimeoutSeconds(AgentPresetConfig preset)
    {
        if (preset.TimeoutSeconds.HasValue && preset.TimeoutSeconds.Value > 0)
            return preset.TimeoutSeconds.Value;

        if (int.TryParse(Environment.GetEnvironmentVariable("OPENAI_TIMEOUT_SECONDS"), out var envSec) && envSec > 0)
            return envSec;

        return 600; // 10 minutes default (increased from 100s / 5m)
    }

    /// <summary>
    /// The wall-clock budget for a single shell command, or <c>null</c> to mean "use the sandbox default".
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ResolveTimeoutSeconds"/> on purpose. That helper answers "how long may we
    /// wait for a model endpoint to answer", and its 600s fallback is right there and wrong here: a hung
    /// <c>dotnet test</c> should be reaped at 180s unless an operator deliberately configured otherwise.
    /// Only an explicit preset value is honoured; the env var is not, because
    /// <c>OPENAI_TIMEOUT_SECONDS</c> is documented as the endpoint budget.
    /// </remarks>
    public static int? ResolveCommandTimeoutSeconds(AgentPresetConfig preset)
    {
        if (preset?.TimeoutSeconds is int configured && configured > 0)
        {
            return configured;
        }

        return null;
    }

    public static int ResolveMaxRetries(AgentPresetConfig preset)
    {
        if (preset.MaxRetries.HasValue && preset.MaxRetries.Value >= 0)
            return preset.MaxRetries.Value;

        if (int.TryParse(Environment.GetEnvironmentVariable("OPENAI_MAX_RETRIES"), out var envRetries) && envRetries >= 0)
            return envRetries;

        return 2; // 2 retries (3 total attempts)
    }

    public static int ResolveMaxSteps(AgentPresetConfig preset)
    {
        if (preset.MaxSteps.HasValue && preset.MaxSteps.Value > 0)
            return preset.MaxSteps.Value;

        if (int.TryParse(Environment.GetEnvironmentVariable("OPENAI_MAX_STEPS"), out var envSteps) && envSteps > 0)
            return envSteps;

        if (int.TryParse(Environment.GetEnvironmentVariable("AGENT_MAX_STEPS"), out var agentSteps) && agentSteps > 0)
            return agentSteps;

        return 100;
    }

    public static int GetRetryDelayMilliseconds(HttpResponseMessage? response, int attempt, AgentPresetConfig? preset = null)
    {
        if (preset != null && preset.RetryDelayMilliseconds.HasValue && preset.RetryDelayMilliseconds.Value >= 0)
            return preset.RetryDelayMilliseconds.Value;

        if (int.TryParse(Environment.GetEnvironmentVariable("OPENAI_RETRY_DELAY_MS"), out var envDelay) && envDelay >= 0)
            return envDelay;

        if (response != null && response.Headers.TryGetValues("Retry-After", out var values))
        {
            var first = values.FirstOrDefault();
            if (int.TryParse(first, out var sec) && sec > 0 && sec <= 60)
            {
                return sec * 1000;
            }
        }

        return Math.Min(15_000, (int)Math.Pow(2, attempt - 1) * 1000);
    }

    public static bool IsRetryableStatusCode(int statusCode)
    {
        return statusCode == 429 || // Too Many Requests / Rate Limited
               statusCode == 500 || // Internal Server Error
               statusCode == 502 || // Bad Gateway
               statusCode == 503 || // Service Unavailable (model loading / warming up)
               statusCode == 504;   // Gateway Timeout
    }

    /// <summary>
    /// Resolves the token budget for a run.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="ModelContextLimits"/> so the API that renders the context picker and
    /// the adapter that honours the choice read one table. They used to hold separate tables, which
    /// is how the picker could advertise 256k while the run was clamped to 128k with nothing in the
    /// response saying so.
    /// </remarks>
    public static int ResolveModelContextLimit(string model, string? contextSizeOption)
        => ModelContextLimits.ResolveTokens(model, contextSizeOption);

    /// <summary>
    /// The credential this adapter would authenticate a run with.
    /// </summary>
    /// <remarks>
    /// Extracted because the capability declaration has to read <c>/models</c> with exactly the
    /// same credential a run uses. If the two resolved keys differently, a driver could declare the
    /// models visible to an ambient <c>OPENAI_API_KEY</c> while running against the key saved on the
    /// preset - a picker listing models the very next prompt would be refused.
    /// </remarks>
    internal static string? ResolveApiKey(AgentPresetConfig preset)
        => !string.IsNullOrWhiteSpace(preset.ApiKey)
            ? preset.ApiKey.Trim()
            : Environment.GetEnvironmentVariable("OPENAI_API_KEY");

    /// <inheritdoc />
    public Task<DriverCapabilityDeclaration> DescribeCapabilitiesAsync(
        AgentPresetConfig preset,
        CancellationToken ct = default)
        => DriverCapabilityDescriber.DescribeOpenAiAsync(preset, ResolveApiKey(preset), ct);

    public static int EstimateMessageTokens(OpenAiMessage message)
    {
        var charCount = (message.Role?.Length ?? 0) + (message.Content?.Length ?? 0);
        if (message.ContentParts != null && message.ContentParts.Count > 0)
        {
            foreach (var part in message.ContentParts)
            {
                if (part is OpenAiTextContentPart textPart && !string.IsNullOrEmpty(textPart.Text))
                {
                    charCount += textPart.Text.Length;
                }
                else
                {
                    charCount += 3600; // ~1000 tokens per image input
                }
            }
        }
        if (message.ToolCalls != null)
        {
            foreach (var tc in message.ToolCalls)
            {
                charCount += (tc.Function?.Name?.Length ?? 0) + (tc.Function?.Arguments?.Length ?? 0) + (tc.Id?.Length ?? 0);
            }
        }
        return (int)Math.Ceiling(charCount / 3.6);
    }

    public static int EstimateTotalTokens(IEnumerable<OpenAiMessage> messages)
    {
        return messages.Sum(EstimateMessageTokens);
    }

    public static (bool IsContextLimit, int? PromptTokens, int? ContextLimit) ParseContextWindowError(string errorContent)
    {
        if (string.IsNullOrWhiteSpace(errorContent)) return (false, null, null);

        // LiteLLM style: "request exceeds the context window: prompt (548337 tokens) leaves no room to answer in the context (524288); requests are never truncated."
        var m1 = Regex.Match(errorContent, @"prompt\s*\(([0-9]+)\s*tokens\).*?context\s*\(([0-9]+)\)", RegexOptions.IgnoreCase);
        if (m1.Success && int.TryParse(m1.Groups[1].Value, out var pt) && int.TryParse(m1.Groups[2].Value, out var cl))
        {
            return (true, pt, cl);
        }

        // OpenAI style: "maximum context length is 131072 tokens. However, your messages resulted in 140000 tokens."
        var m2 = Regex.Match(errorContent, @"maximum context length is\s*([0-9]+)\s*tokens.*?resulted in\s*([0-9]+)\s*tokens", RegexOptions.IgnoreCase);
        if (m2.Success && int.TryParse(m2.Groups[1].Value, out var cl2) && int.TryParse(m2.Groups[2].Value, out var pt2))
        {
            return (true, pt2, cl2);
        }

        // Generic context limit regex
        var m3 = Regex.Match(errorContent, @"context\s*length\s*exceeded.*?([0-9]+)", RegexOptions.IgnoreCase);
        if (m3.Success && int.TryParse(m3.Groups[1].Value, out var cl3))
        {
            return (true, null, cl3);
        }

        if (errorContent.Contains("exceeds the context window", StringComparison.OrdinalIgnoreCase) ||
            errorContent.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase) ||
            errorContent.Contains("maximum context length", StringComparison.OrdinalIgnoreCase) ||
            (errorContent.Contains("context", StringComparison.OrdinalIgnoreCase) && errorContent.Contains("too large", StringComparison.OrdinalIgnoreCase)))
        {
            return (true, null, null);
        }

        return (false, null, null);
    }

    /// <summary>
    /// True when the dispatch carried a prompt stack complete enough to build a payload from.
    /// </summary>
    /// <remarks>
    /// Both halves are required. A stack with a prefix but no envelope would leave the agent with
    /// identity and conventions and nothing to do; one with an envelope but no prefix would leave it
    /// with a task and no persona. Either is worse than falling back to the legacy single-prompt path,
    /// which is the behaviour that shipped before this field existed.
    /// </remarks>
    internal static bool HasUsableStack(PromptStackWirePayload? stack) =>
        stack is not null &&
        !string.IsNullOrWhiteSpace(stack.CachedSystemPrompt) &&
        !string.IsNullOrWhiteSpace(stack.UserTaskEnvelope);

    /// <summary>
    /// The first line of the turn-0 task envelope, used to recognise it after a disk round trip.
    /// </summary>
    /// <remarks>
    /// Matched on the composer's own literal rather than on a marker we add, because the history list is
    /// serialised to disk and reloaded between turns: instance identity, object tags and any
    /// out-of-band flag are gone by then, and a content prefix is the only thing that survives the
    /// round trip. It costs no extra bytes, which matters because it is compared against the leading
    /// characters of a message that is already in every cached prefix.
    /// </remarks>
    internal const string TaskEnvelopeMarker = "[TASK ENVELOPE & WORKING ENVIRONMENT]";

    /// <summary>True when this message is the turn-0 task envelope.</summary>
    internal static bool IsTaskEnvelope(OpenAiMessage? message) =>
        message is { Role: "user" } &&
        message.Content != null &&
        message.Content.StartsWith(TaskEnvelopeMarker, StringComparison.Ordinal);

    /// <summary>True when this message is a model-written compaction summary.</summary>
    internal static bool IsPinnedSummary(OpenAiMessage? message) =>
        message is { Role: "system" } &&
        message.Content != null &&
        message.Content.StartsWith(SummaryMarker, StringComparison.Ordinal);

    /// <summary>
    /// The first message compaction is allowed to prune.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Index 0 is the cached system prefix and is already skipped by the existing code. What it did
    /// <em>not</em> skip is index 1 — which, before the split, was always a prunable conversation turn,
    /// and which is now the task envelope.
    /// </para>
    /// <para>
    /// That made the pre-existing compaction pass actively hostile to this job's purpose:
    /// <c>CompactHistory</c> truncates messages from index 1 down to a 200–400 character snippet and
    /// then deletes them outright, and <c>TrySummarizeOldTurnsAsync</c> issues
    /// <c>RemoveRange(1, cutoff - 1)</c>. On a long turn — the turns where compaction actually fires —
    /// the envelope holding the task id, the acceptance criteria, the working root and the git recovery
    /// note would be the first casualty. The agent would then be mid-session with no task, which is the
    /// precise "waiting for the user's question" idle loop this feature exists to eliminate, arriving
    /// later in the session than the bug it replaced and with a cleaner-looking transcript.
    /// </para>
    /// <para>
    /// Scanning forward past both the envelope and any pinned summary keeps the protection correct in
    /// either order: the summariser inserts at index 1, which shifts an envelope that was there to
    /// index 2.
    /// </para>
    /// </remarks>
    internal static int FirstPrunableIndex(List<OpenAiMessage> history)
    {
        var index = 1;
        while (index < history.Count && (IsTaskEnvelope(history[index]) || IsPinnedSummary(history[index])))
        {
            index++;
        }

        return index;
    }

    /// <summary>
    /// Repairs a payload that would end on a system message, returning how many messages moved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The strict boundary rule: the final message before generation must be <c>user</c> or
    /// <c>tool</c>. An instruction-tuned model that reads a system message last concludes that
    /// environment setup has <em>just</em> completed and that nobody has asked it anything yet, so it
    /// replies that it is waiting for instructions and the turn burns itself doing nothing.
    /// </para>
    /// <para>
    /// Repairing rather than throwing is deliberate. A trailing system message is a defect in the
    /// payload, not in the task: failing the turn converts a malformed prompt into a halted workflow
    /// and a retried agent, while the model never got a chance to do the work it was given. Merging the
    /// stray text into the leading system prefix keeps every byte of the instruction, keeps it in the
    /// position a system instruction belongs, and leaves the array ending on a real turn.
    /// </para>
    /// <para>
    /// Appending to the end of <c>history[0]</c> is also the cheapest place for the cache to put it: a
    /// prefix cache matches on leading tokens, so the whole of layers 1–5 still hits and only the
    /// appended text misses. Relocating the message to the front of the array, or promoting it to its
    /// own second system message, would diverge earlier and cost more.
    /// </para>
    /// <para>
    /// The count is returned so the caller can log it. Silently relocating would hide the producer that
    /// appended the message in the first place, and the whole point of the guard is to make that
    /// producer findable.
    /// </para>
    /// </remarks>
    internal static int SanitizeMessageOrder(List<OpenAiMessage> history)
    {
        var relocated = 0;

        // Loop rather than a single pass: two appended system messages would otherwise leave the second
        // one trailing after the first is moved, and the rule is about the last message, not about how
        // many were wrong.
        while (history.Count > 1 && history[^1].Role == "system")
        {
            var stray = history[^1];
            history.RemoveAt(history.Count - 1);

            if (history[0].Role == "system")
            {
                history[0].Content = string.IsNullOrEmpty(history[0].Content)
                    ? stray.Content
                    : history[0].Content + "\n\n" + stray.Content;
            }
            else
            {
                history.Insert(0, stray);
            }

            relocated++;
        }

        return relocated;
    }

    public static bool CompactHistory(List<OpenAiMessage> history, int targetMaxTokens, bool forceAggressive = false)
    {
        if (history.Count <= 1) return false;

        var modified = false;
        var protectedTailCount = forceAggressive ? 2 : 4;

        // Everything at or before FirstPrunableIndex is pinned: the cached system prefix and, since the
        // split, the turn-0 task envelope sitting at index 1. The tail bound is computed from that
        // floor, so a short history cannot have its envelope swallowed by the protected tail either.
        var floor = FirstPrunableIndex(history);
        var cutoff = Math.Max(floor, history.Count - protectedTailCount);

        // Phase 1: Truncate older tool output messages
        for (int i = floor; i < cutoff; i++)
        {
            var msg = history[i];
            var maxLen = forceAggressive ? 200 : 400;
            if (msg.Role == "tool" && !string.IsNullOrEmpty(msg.Content) && msg.Content.Length > maxLen)
            {
                var snippetLen = forceAggressive ? 100 : 200;
                var snippet = msg.Content.Length > snippetLen ? msg.Content[..snippetLen].Trim() : msg.Content;
                msg.Content = $"[Tool output compacted to preserve context: {snippet}... (trimmed)]";
                modified = true;
            }
            else if (msg.Role == "user" && msg.Content != null && msg.Content.StartsWith("Tool Result for ") && msg.Content.Length > maxLen)
            {
                var snippetLen = forceAggressive ? 100 : 200;
                var snippet = msg.Content.Length > snippetLen ? msg.Content[..snippetLen].Trim() : msg.Content;
                msg.Content = $"[Tool output compacted to preserve context: {snippet}... (trimmed)]";
                modified = true;
            }
        }

        if (EstimateTotalTokens(history) <= targetMaxTokens && modified && !forceAggressive)
            return true;

        // Phase 2: If still over limit, slide older message turns atomically.
        // `floor` already accounts for a pinned summary and for the task envelope, both of which are
        // re-derived above rather than re-detected here — one definition of "pinned" is the point.
        var first = floor;
        var prunedTurns = 0;
        while (history.Count > (protectedTailCount + first) && EstimateTotalTokens(history) > targetMaxTokens)
        {
            var msg1 = history[first];
            if (msg1.Role == "user")
            {
                history.RemoveAt(first);
                prunedTurns++;
                modified = true;
            }
            else if (msg1.Role == "assistant")
            {
                history.RemoveAt(first);
                modified = true;
                while (history.Count > first && history[first].Role == "tool")
                {
                    history.RemoveAt(first);
                }
                prunedTurns++;
            }
            else
            {
                history.RemoveAt(first);
                modified = true;
            }
        }

        if (prunedTurns > 0)
        {
            history.Insert(first, new OpenAiMessage
            {
                Role = "system",
                Content = $"[Context Compaction: {prunedTurns} earlier conversation turns were pruned to keep within the model context window. Work continues from recent context below.]"
            });
        }

        // Phase 3: For aggressive context recovery, truncate any oversized individual message
        if (forceAggressive)
        {
            for (int i = 1; i < history.Count; i++)
            {
                var msg = history[i];
                var estTokens = EstimateMessageTokens(msg);
                if (estTokens > targetMaxTokens && !string.IsNullOrEmpty(msg.Content))
                {
                    var maxChars = Math.Max(2000, targetMaxTokens * 3);
                    if (msg.Content.Length > maxChars)
                    {
                        msg.Content = TruncateToolResult(msg.Content, maxChars);
                        modified = true;
                    }
                }
            }

            return true;
        }

        return modified;
    }

    /// <summary>
    /// Prefix of a model-written compaction summary pinned after the system prompt.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so a test can build a history in the exact shape the summariser
    /// leaves behind. Repeating the literal in the test would make it pass against a stale copy of the
    /// string while the real pairing silently broke.
    /// </remarks>
    internal const string SummaryMarker = "[Conversation Summary";
    private const int SummaryTranscriptMaxChars = 120_000;

    /// <summary>
    /// Replaces older turns (everything between the system prompt and the protected tail) with a single
    /// model-written summary. The summarizer request is bounded in size, so it stays safe even when the
    /// full history is too large for the server. Returns false (history untouched) on any failure.
    /// </summary>
    public static async Task<bool> TrySummarizeOldTurnsAsync(
        List<OpenAiMessage> history,
        int protectedTailCount,
        string chatUrl,
        string model,
        string? apiKey,
        AgentPresetConfig preset,
        int timeoutSeconds,
        CancellationToken ct)
    {
        if (history.Count <= 2) return false;

        // Same floor as CompactHistory: the cached prefix, the task envelope and any existing summary
        // are all pinned. Without this, the summariser would fold the envelope into its own summary
        // text and then delete the original — leaving the agent with a prose paraphrase of its task in
        // place of the task id, the acceptance criteria and the working root it is supposed to use.
        var floor = FirstPrunableIndex(history);
        if (history.Count - floor <= protectedTailCount) return false;

        var cutoff = Math.Max(floor, history.Count - protectedTailCount);
        // Never start the kept tail on a tool message (would orphan it from its assistant tool call).
        while (cutoff > floor && cutoff < history.Count && history[cutoff].Role == "tool") cutoff--;
        if (cutoff <= floor) return false;

        var middle = history.GetRange(floor, cutoff - floor);

        // Build a bounded transcript. Prior summaries come first; recent turns are favored over old ones.
        static string Clip(string? s, int max) =>
            string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..(max / 2)] + " …[clipped]… " + s[^(max / 2)..];

        var priorSummary = new StringBuilder();
        var lines = new List<string>();
        foreach (var m in middle)
        {
            if (m.Role == "system" && m.Content != null && m.Content.StartsWith(SummaryMarker, StringComparison.Ordinal))
            {
                priorSummary.AppendLine(Clip(m.Content, 20_000));
                continue;
            }
            var sb = new StringBuilder();
            sb.Append('[').Append(m.Role).Append("] ");
            sb.Append(Clip(m.Content, m.Role == "tool" ? 600 : 4_000));
            if (m.ToolCalls != null)
            {
                foreach (var tc in m.ToolCalls)
                    sb.Append($"\n  -> tool_call {tc.Function?.Name}({Clip(tc.Function?.Arguments, 300)})");
            }
            lines.Add(sb.ToString());
        }

        var kept = new List<string>();
        var budget = SummaryTranscriptMaxChars;
        for (int i = lines.Count - 1; i >= 0 && budget > 0; i--)
        {
            budget -= lines[i].Length;
            kept.Add(lines[i]);
        }
        kept.Reverse();

        var transcript = new StringBuilder();
        if (priorSummary.Length > 0)
            transcript.AppendLine("PREVIOUS SUMMARY:\n" + priorSummary + "\n");
        if (kept.Count < lines.Count)
            transcript.AppendLine($"(… {lines.Count - kept.Count} older messages omitted for length …)\n");
        transcript.AppendLine("CONVERSATION:");
        foreach (var l in kept) transcript.AppendLine(l).AppendLine();

        const string summarizerPrompt =
            "You compress an AI coding agent's conversation history so the agent can continue seamlessly. " +
            "Write a dense, factual summary in Markdown covering: the user's goals and instructions (verbatim for key constraints), " +
            "decisions made, files/paths/commands/identifiers touched, what was completed, errors encountered and their resolutions, " +
            "current state, and outstanding TODOs / next steps. Merge any PREVIOUS SUMMARY into your output. " +
            "Do not invent details. Do not address the user. Output only the summary.";

        var req = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new Dictionary<string, object?> { ["role"] = "system", ["content"] = summarizerPrompt },
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = transcript.ToString() }
            },
            ["stream"] = false,
            ["max_tokens"] = 3000
        };

        try
        {
            using var httpReq = new HttpRequestMessage(HttpMethod.Post, chatUrl);
            if (!string.IsNullOrWhiteSpace(apiKey))
                httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            if (preset.CustomHeaders != null)
                foreach (var kvp in preset.CustomHeaders)
                    httpReq.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
            httpReq.Content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(120, timeoutSeconds)));
            using var res = await HttpClientInstance.SendAsync(httpReq, cts.Token);
            if (!res.IsSuccessStatusCode) return false;

            var body = await res.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(body);
            var summary = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(summary)) return false;

            // Rebuild from `floor`, not from index 1: the pinned prefix, envelope and prior summary must
            // survive the fold. Inserting the new summary at `floor` keeps it directly after the
            // envelope, which is where CompactHistory expects to find it on the next pass.
            history.RemoveRange(floor, cutoff - floor);
            history.Insert(floor, new OpenAiMessage
            {
                Role = "system",
                Content = $"{SummaryMarker} — {middle.Count} earlier messages were compacted into this summary]\n{summary.Trim()}"
            });
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public static string TruncateToolResult(string result, int maxChars = 50_000)
    {
        if (string.IsNullOrEmpty(result) || result.Length <= maxChars) return result;
        var headLength = maxChars / 2;
        var tailLength = maxChars / 3;
        var omitted = result.Length - (headLength + tailLength);
        return $"{result[..headLength]}\n\n[... Output truncated: {omitted} characters omitted to preserve context window ...]\n\n{result[^tailLength..]}";
    }

    public async Task ExecutePromptAsync(
        AgentPresetConfig preset,
        PromptReceivedEvent promptEvent,
        Func<TrajectoryEventPayload, Task> onEvent,
        CancellationToken ct)
    {
        var cwd = !string.IsNullOrWhiteSpace(promptEvent.WorkingDirectory)
            ? promptEvent.WorkingDirectory
            : AgentAdapterFactory.ResolveWorkspaceDirectory();

        var endpoint = !string.IsNullOrWhiteSpace(preset.Endpoint)
            ? preset.Endpoint.Trim()
            : Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? "http://localhost:11434/v1";

        endpoint = endpoint.TrimEnd('/');
        var chatUrl = endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? endpoint
            : $"{endpoint}/chat/completions";

        var apiKey = ResolveApiKey(preset);

        var effectiveModel = !string.IsNullOrWhiteSpace(promptEvent.Model) &&
                             !promptEvent.Model.Equals("default", StringComparison.OrdinalIgnoreCase) &&
                             !promptEvent.Model.Equals("custom", StringComparison.OrdinalIgnoreCase)
            ? promptEvent.Model.Trim()
            : !string.IsNullOrWhiteSpace(preset.DefaultModel)
                ? preset.DefaultModel.Trim()
                : "llama3.3";

        var timeoutSeconds = ResolveTimeoutSeconds(preset);
        var maxRetries = ResolveMaxRetries(preset);
        var modelContextLimit = ResolveModelContextLimit(effectiveModel, promptEvent.ContextSize);

        ct.ThrowIfCancellationRequested();
        await onEvent(new TrajectoryEventPayload
        {
            EventType = "thought",
            Content = $"[@{preset.Id}] Connecting to OpenAI-compatible server at {endpoint} using model '{effectiveModel}' (timeout: {timeoutSeconds}s, context: {modelContextLimit} tokens)..."
        });

        // Initialize or retrieve session history
        var history = SessionHistories.GetOrAdd(promptEvent.SessionId, sid => LoadHistoryFromDisk(sid) ?? new List<OpenAiMessage>());
        if (history.Count == 0)
        {
            // Two system-prompt sources, in order of authority.
            //
            // The composed stack wins because it is built against the database: it carries the persona
            // row, the project manifest, the job roster, the agent's saved memories and the platform
            // invariants, in volatility order so the leading bytes are reusable. The locally built
            // prompt has the persona text the dispatcher happened to put on the wire and nothing else —
            // which is why an agent resumed on a new pod forgot every convention it had been told.
            //
            // The local builder stays as the fallback rather than being deleted: a dispatch from an API
            // that predates the split, or from a path with no composer (job chat, handoff), still has
            // to produce a usable prompt, and an agent with no system message at all is worse than one
            // with a thinner one.
            var stack = promptEvent.PromptStack;
            var systemPrompt = HasUsableStack(stack)
                ? stack!.CachedSystemPrompt
                : BuildSystemPrompt(preset, promptEvent, cwd);

            history.Add(new OpenAiMessage { Role = "system", Content = systemPrompt });

            // Seed the sliding window the composer sent.
            //
            // This is not an optimisation, it is the only continuity a macro turn has. Every macro turn
            // mints a brand-new turn session, so `history` here is empty at the start of turn 2, 3 and
            // 4 — the agent's own previous turns are in `session_messages` under a session id this turn
            // will never look at. Without the seed, turn N reads as turn 1 every time: the agent
            // re-reads the file it already fixed, re-runs the test it already watched fail, and reports
            // progress as if nothing preceded it.
            //
            // Seeded before the envelope is appended so the envelope stays the last user turn, which is
            // what makes it the thing the model answers.
            if (HasUsableStack(stack))
            {
                foreach (var message in stack!.History ?? Array.Empty<TurnHistoryMessage>())
                {
                    if (string.IsNullOrWhiteSpace(message.Content)) continue;

                    // Roles are coerced to the two the window is allowed to occupy. The ordering rule is
                    // "system first, envelope second", which means exactly one system message and it
                    // leads the array — so a window row may not be one either.
                    //
                    // `tool` is never emitted by the composer: tool results are excluded from the window
                    // precisely because the harness stores no tool_call_id, and an orphan tool message is
                    // a 400 from every OpenAI-compatible server.
                    //
                    // `system` is likewise excluded upstream, as harness bookkeeping rather than
                    // conversation. Coercing rather than dropping keeps the guard at the boundary
                    // deterministic across composer versions: a window written by an older build that
                    // leaked a system row still produces a legal payload here, and the text survives as
                    // a user turn instead of vanishing from the transcript.
                    var role = message.Role?.ToLowerInvariant() switch
                    {
                        "assistant" => "assistant",
                        "user" => "user",
                        null or "" => "user",
                        _ => "user",
                    };

                    history.Add(new OpenAiMessage { Role = role, Content = message.Content });
                }
            }

            SaveHistoryToDisk(promptEvent.SessionId, history);
        }

        var attachedBlock = AgentAdapterFactory.FormatAttachedFilesBlock(promptEvent.Attachments);

        // The user turn's body depends on which prompt shape arrived.
        //
        // With a stack, the body is Layer 6 alone. `promptEvent.Prompt` in that case is the *flattened*
        // stack — layers 1 through 6 concatenated, because that is the shape the session row stores and
        // the shape a pre-split binary needs — so using it here would resend layers 1–5 a second time,
        // inside the user turn, immediately after sending them as the system message. The agent would
        // read its own persona and project manifest twice, and the byte-for-byte duplicate would sit
        // right where the model puts most of its attention.
        //
        // Without a stack, `promptEvent.Prompt` is all there is, and it goes in whole.
        var stackForUserTurn = promptEvent.PromptStack;
        var promptBody = HasUsableStack(stackForUserTurn)
            ? stackForUserTurn!.UserTaskEnvelope
            : promptEvent.Prompt;

        var fullUserPrompt = string.IsNullOrWhiteSpace(attachedBlock)
            ? promptBody
            : $"{attachedBlock}{promptBody}";

        var imageParts = new List<object>();
        if (promptEvent.Attachments != null && promptEvent.Attachments.Count > 0)
        {
            foreach (var att in promptEvent.Attachments)
            {
                var filePath = att.LocalFilePath;
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    var attachDir = Path.Combine(cwd, ".agentmesh", "attachments");
                    if (Directory.Exists(attachDir))
                    {
                        var safeName = Path.GetFileName(att.FileName);
                        var candidate = Directory.GetFiles(attachDir, $"*{safeName}*").FirstOrDefault();
                        if (candidate != null && File.Exists(candidate)) filePath = candidate;
                    }
                }

                if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
                {
                    var ext = Path.GetExtension(filePath).ToLowerInvariant();
                    if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp")
                    {
                        var mime = ext switch
                        {
                            ".png" => "image/png",
                            ".jpg" or ".jpeg" => "image/jpeg",
                            ".gif" => "image/gif",
                            ".webp" => "image/webp",
                            _ => "image/png"
                        };
                        try
                        {
                            var bytes = File.ReadAllBytes(filePath);
                            var base64 = Convert.ToBase64String(bytes);
                            imageParts.Add(new OpenAiImageUrlContentPart
                            {
                                Type = "image_url",
                                ImageUrl = new OpenAiImageUrl
                                {
                                    Url = $"data:{mime};base64,{base64}"
                                }
                            });
                        }
                        catch { }
                    }
                }
            }
        }

        if (imageParts.Count > 0)
        {
            var contentParts = new List<object>
            {
                new OpenAiTextContentPart { Type = "text", Text = fullUserPrompt }
            };
            contentParts.AddRange(imageParts);

            history.Add(new OpenAiMessage
            {
                Role = "user",
                Content = fullUserPrompt,
                ContentParts = contentParts
            });
        }
        else
        {
            history.Add(new OpenAiMessage { Role = "user", Content = fullUserPrompt });
        }
        SaveHistoryToDisk(promptEvent.SessionId, history);

        var isDirectMode = string.Equals(promptEvent.Mode, "direct", StringComparison.OrdinalIgnoreCase);
        var toolsDefinition = isDirectMode ? null : GetToolDefinitions();

        // Compaction: summarize older turns with the model (lossy fallback to truncation), then persist.
        async Task<(bool Changed, bool Summarized)> CompactAsync(int target, bool aggressive)
        {
            var summarized = await TrySummarizeOldTurnsAsync(history, aggressive ? 2 : 4, chatUrl, effectiveModel, apiKey, preset, timeoutSeconds, ct);
            var changed = CompactHistory(history, target, aggressive);
            if (changed || summarized) SaveHistoryToDisk(promptEvent.SessionId, history);
            return (changed || summarized, summarized);
        }

        var maxSteps = ResolveMaxSteps(preset);
        var stepCount = 0;
        var streamRetryAttempt = 0;
        var responseEmitted = false;
        var askedQuestion = false;
        long totalInputTokens = 0;
        long totalOutputTokens = 0;
        long totalTokens = 0;
        var sw = Stopwatch.StartNew();

        while (stepCount < maxSteps)
        {
            stepCount++;
            ct.ThrowIfCancellationRequested();

            // Proactive history compaction
            var targetMaxTokens = (int)(modelContextLimit * 0.80);
            if (EstimateTotalTokens(history) > targetMaxTokens)
            {
                var beforeEst = EstimateTotalTokens(history);
                var (compacted, summarized) = await CompactAsync(targetMaxTokens, false);
                if (compacted)
                {
                    var afterEst = EstimateTotalTokens(history);
                    await onEvent(new TrajectoryEventPayload
                    {
                        EventType = "thought",
                        Content = $"[@{preset.Id}] Proactively {(summarized ? "summarized" : "truncated")} conversation history ({beforeEst} -> {afterEst} tokens) to remain within {modelContextLimit} context limit."
                    });
                }
            }

            HttpResponseMessage? httpRes = null;
            var retryAttempt = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // Strict boundary rule, enforced at the last moment before serialisation.
                //
                // Applied to a snapshot rather than to `history` itself: the relocation merges a stray
                // system message into the leading prefix, and mutating the live list would compound
                // that merge on every retry of the same request and on every tool-loop iteration. The
                // snapshot is what gets serialised, so repairing it is what the model actually sees.
                //
                // Inside the loop, not before it, because `history` grows during the tool round-trips
                // and a system message can be appended by any of them — which is exactly how the
                // original defect reached the wire.
                var dispatchMessages = history.Select(m => m.Clone()).ToList();
                var relocated = SanitizeMessageOrder(dispatchMessages);
                if (relocated > 0)
                {
                    // Emitted as a thought rather than swallowed: the guard's value is in making
                    // whichever producer appended the message findable. The turn still runs — a
                    // malformed payload is a defect in the prompt, not a reason to halt a workflow and
                    // re-run an agent.
                    await onEvent(new TrajectoryEventPayload
                    {
                        EventType = "thought",
                        Content = $"[@{preset.Id}] [Harness guard] Relocated {relocated} trailing " +
                                  "system message(s) into the leading system block before dispatch. A " +
                                  "system message at the tail makes instruction-tuned models wait for a " +
                                  "user prompt instead of driving the task — report this as a producer " +
                                  "defect."
                    });
                }

                var reqBody = new Dictionary<string, object?>
                {
                    ["model"] = effectiveModel,
                    ["messages"] = dispatchMessages.Select(m => m.ToSerializableObject()).ToList(),
                    ["stream"] = true
                };

                if (toolsDefinition != null && toolsDefinition.Count > 0)
                {
                    reqBody["tools"] = toolsDefinition;
                    reqBody["tool_choice"] = "auto";
                }

                using var httpReq = new HttpRequestMessage(HttpMethod.Post, chatUrl);
                httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }

                if (preset.CustomHeaders != null)
                {
                    foreach (var kvp in preset.CustomHeaders)
                    {
                        httpReq.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
                    }
                }

                var jsonContent = JsonSerializer.Serialize(reqBody);
                httpReq.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                try
                {
                    httpRes = await HttpClientInstance.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
                {
                    if (retryAttempt < maxRetries)
                    {
                        retryAttempt++;
                        var delayMs = GetRetryDelayMilliseconds(null, retryAttempt, preset);
                        await onEvent(new TrajectoryEventPayload
                        {
                            EventType = "thought",
                            Content = $"[@{preset.Id}] Initial handshake to OpenAI endpoint timed out after {timeoutSeconds}s (likely loading weights/experts into VRAM). Retrying in {delayMs / 1000.0:F1}s (attempt {retryAttempt}/{maxRetries})..."
                        });
                        await Task.Delay(delayMs, ct);
                        continue;
                    }

                    var errorMsg = $"Initial handshake to OpenAI-compatible endpoint ({chatUrl}) timed out after {timeoutSeconds}s. The server may still be loading weights/experts into VRAM or under heavy load.";
                    await onEvent(new TrajectoryEventPayload
                    {
                        EventType = "response",
                        Content = $"❌ {errorMsg}\n\nYou can increase the timeout by setting `timeoutSeconds` in the agent config or the `OPENAI_TIMEOUT_SECONDS` environment variable."
                    });
                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is IOException)
                {
                    if (retryAttempt < maxRetries)
                    {
                        retryAttempt++;
                        var delayMs = GetRetryDelayMilliseconds(null, retryAttempt, preset);
                        await onEvent(new TrajectoryEventPayload
                        {
                            EventType = "thought",
                            Content = $"[@{preset.Id}] Network error connecting to OpenAI endpoint ({ex.Message}). Retrying in {delayMs / 1000.0:F1}s (attempt {retryAttempt}/{maxRetries})..."
                        });
                        await Task.Delay(delayMs, ct);
                        continue;
                    }

                    var errorMsg = $"Failed to connect to OpenAI-compatible endpoint ({chatUrl}): {ex.Message}";
                    var isLocal = endpoint.Contains("localhost") || endpoint.Contains("127.0.0.1");
                    var extraHint = isLocal
                        ? "\n\n💡 If using a driver companion daemon or local proxy (such as `aip proxy`), ensure the companion daemon is active and signed in (`aip login`). You can test and kickstart it via 'Verify Driver' or the interactive terminal in the Persona Environment modal."
                        : "";
                    await onEvent(new TrajectoryEventPayload
                    {
                        EventType = "response",
                        Content = $"❌ {errorMsg}\n\nPlease verify that your server is running and accessible at `{endpoint}`.{extraHint}"
                    });
                    return;
                }

                if (!httpRes.IsSuccessStatusCode)
                {
                    var errContent = await httpRes.Content.ReadAsStringAsync(ct);
                    var statusCode = (int)httpRes.StatusCode;

                    var (isContextErr, promptTokens, parsedLimit) = ParseContextWindowError(errContent);
                    if (isContextErr)
                    {
                        var effectiveLimit = parsedLimit ?? modelContextLimit;
                        if (parsedLimit.HasValue && parsedLimit.Value > 0)
                        {
                            ModelContextLimits.RecordLearnedLimit(effectiveModel, parsedLimit.Value);
                            modelContextLimit = Math.Min(modelContextLimit, parsedLimit.Value);
                            var reporter = LearnedContextLimitReporter;
                            var reportDriverId = preset.Id;
                            var reportModel = effectiveModel;
                            var reportLimit = parsedLimit.Value;
                            if (reporter != null && !string.IsNullOrWhiteSpace(reportDriverId))
                            {
                                _ = Task.Run(async () =>
                                {
                                    try { await reporter(reportDriverId, reportModel, reportLimit); } catch { }
                                });
                            }
                        }

                        var targetTokens = (int)(effectiveLimit * 0.70);
                        await onEvent(new TrajectoryEventPayload
                        {
                            EventType = "thought",
                            Content = $"[@{preset.Id}] Context window exceeded ({promptTokens?.ToString() ?? "prompt"} tokens vs {effectiveLimit} limit). Compacting history and retrying..."
                        });

                        var (compacted, _) = await CompactAsync(targetTokens, true);
                        if (compacted)
                        {
                            httpRes.Dispose();
                            continue;
                        }
                    }

                    if (IsRetryableStatusCode(statusCode) && retryAttempt < maxRetries)
                    {
                        retryAttempt++;
                        var delayMs = GetRetryDelayMilliseconds(httpRes, retryAttempt, preset);
                        await onEvent(new TrajectoryEventPayload
                        {
                            EventType = "thought",
                            Content = $"[@{preset.Id}] OpenAI server returned HTTP {statusCode} ({httpRes.ReasonPhrase}). Retrying in {delayMs / 1000.0:F1}s (attempt {retryAttempt}/{maxRetries})..."
                        });
                        httpRes.Dispose();
                        await Task.Delay(delayMs, ct);
                        continue;
                    }

                    var errorMsg = $"OpenAI server error (HTTP {statusCode} {httpRes.ReasonPhrase}): {errContent}";
                    await onEvent(new TrajectoryEventPayload
                    {
                        EventType = "response",
                        Content = $"❌ {errorMsg}"
                    });
                    httpRes.Dispose();
                    return;
                }

                break;
            }

            using var activeResponse = httpRes!;

            var contentBuilder = new StringBuilder();
            var reasoningBuilder = new StringBuilder();
            var toolCallsByIndex = new SortedDictionary<int, (string? Id, StringBuilder Name, StringBuilder Args)>();

            Exception? streamDropped = null;
            using (var stream = await activeResponse.Content.ReadAsStreamAsync(ct))
            using (var reader = new StreamReader(stream))
            {
                var streamReadTimeout = TimeSpan.FromSeconds(Math.Max(120, timeoutSeconds));
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(streamReadTimeout);

                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(readCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested && readCts.IsCancellationRequested)
                    {
                        await onEvent(new TrajectoryEventPayload
                        {
                            EventType = "thought",
                            Content = $"[@{preset.Id}] Streaming response timed out after {streamReadTimeout.TotalSeconds:F0}s of inactivity from server."
                        });
                        break;
                    }
                    catch (Exception ex) when (ex is IOException || ex is HttpRequestException)
                    {
                        // Server/proxy dropped the stream mid-response (e.g. "ResponseEnded"),
                        // typically after a very large prompt prefill. Discard partial output and retry the step.
                        streamDropped = ex;
                        break;
                    }

                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

                    var data = trimmed.Length > 5 ? trimmed[5..].Trim() : "";
                    if (data == "[DONE]") break;

                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        var root = doc.RootElement;

                        // Check usage in chunk
                        if (root.TryGetProperty("usage", out var usageElem) && usageElem.ValueKind == JsonValueKind.Object)
                        {
                            if (usageElem.TryGetProperty("prompt_tokens", out var pt) || usageElem.TryGetProperty("input_tokens", out pt))
                                totalInputTokens = pt.GetInt64();
                            if (usageElem.TryGetProperty("completion_tokens", out var ctProp) || usageElem.TryGetProperty("output_tokens", out ctProp))
                                totalOutputTokens = ctProp.GetInt64();
                            if (usageElem.TryGetProperty("total_tokens", out var tt))
                                totalTokens = tt.GetInt64();
                        }

                        if (!root.TryGetProperty("choices", out var choicesElem) || choicesElem.GetArrayLength() == 0)
                            continue;

                        var choice = choicesElem[0];
                        if (!choice.TryGetProperty("delta", out var deltaElem)) continue;

                        // Delta content
                        if (deltaElem.TryGetProperty("content", out var cProp) && cProp.ValueKind == JsonValueKind.String)
                        {
                            var textChunk = cProp.GetString();
                            if (!string.IsNullOrEmpty(textChunk))
                            {
                                contentBuilder.Append(textChunk);
                            }
                        }

                        // Delta reasoning / thought (DeepSeek-R1, Qwen reasoning, etc.)
                        if ((deltaElem.TryGetProperty("reasoning_content", out var rcProp) ||
                             deltaElem.TryGetProperty("reasoning", out rcProp) ||
                             deltaElem.TryGetProperty("thought", out rcProp)) &&
                            rcProp.ValueKind == JsonValueKind.String)
                        {
                            var reasoningChunk = rcProp.GetString();
                            if (!string.IsNullOrEmpty(reasoningChunk))
                            {
                                reasoningBuilder.Append(reasoningChunk);
                            }
                        }

                        // Delta tool_calls
                        if (deltaElem.TryGetProperty("tool_calls", out var tcProp) && tcProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var tcElem in tcProp.EnumerateArray())
                            {
                                var idx = tcElem.TryGetProperty("index", out var iProp) ? iProp.GetInt32() : 0;
                                if (!toolCallsByIndex.TryGetValue(idx, out var tuple))
                                {
                                    tuple = (null, new StringBuilder(), new StringBuilder());
                                    toolCallsByIndex[idx] = tuple;
                                }

                                if (tcElem.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                                {
                                    tuple.Id = idProp.GetString();
                                }

                                if (tcElem.TryGetProperty("function", out var fnElem))
                                {
                                    if (fnElem.TryGetProperty("name", out var fnNameProp) && fnNameProp.ValueKind == JsonValueKind.String)
                                    {
                                        tuple.Name.Append(fnNameProp.GetString());
                                    }
                                    if (fnElem.TryGetProperty("arguments", out var fnArgsProp) && fnArgsProp.ValueKind == JsonValueKind.String)
                                    {
                                        tuple.Args.Append(fnArgsProp.GetString());
                                    }
                                }

                                toolCallsByIndex[idx] = tuple;
                            }
                        }
                    }
                    catch
                    {
                        // Ignore malformed chunk
                    }
                }
            }

            if (streamDropped != null)
            {
                if (streamRetryAttempt < maxRetries)
                {
                    streamRetryAttempt++;
                    var delayMs = GetRetryDelayMilliseconds(null, streamRetryAttempt, preset);
                    await onEvent(new TrajectoryEventPayload
                    {
                        EventType = "thought",
                        Content = $"[@{preset.Id}] Response stream dropped ({streamDropped.Message}). Retrying in {delayMs / 1000.0:F1}s (attempt {streamRetryAttempt}/{maxRetries})..."
                    });
                    // A deterministic failure on a huge prompt won't fix itself: shrink the history
                    // on every retry (halving the estimate) so the next attempt is actually different.
                    var estBefore = EstimateTotalTokens(history);
                    var shrinkTarget = Math.Max(8_000, estBefore / 2);
                    if ((await CompactAsync(shrinkTarget, true)).Changed)
                    {
                        await onEvent(new TrajectoryEventPayload
                        {
                            EventType = "thought",
                            Content = $"[@{preset.Id}] Shrunk history ({estBefore} -> {EstimateTotalTokens(history)} est. tokens) before retrying."
                        });
                    }
                    await Task.Delay(delayMs, ct);
                    stepCount--;
                    continue;
                }

                await onEvent(new TrajectoryEventPayload
                {
                    EventType = "response",
                    Content = $"❌ Response stream from {chatUrl} ended prematurely ({streamDropped.Message}) after {streamRetryAttempt} retries. The prompt (~{EstimateTotalTokens(history)} est. tokens) may be too large for the server or a proxy timed out during prefill."
                });
                return;
            }
            streamRetryAttempt = 0;

            // If reasoning occurred, emit thought event
            if (reasoningBuilder.Length > 0)
            {
                var reasoningText = reasoningBuilder.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(reasoningText))
                {
                    await onEvent(new TrajectoryEventPayload
                    {
                        EventType = "thought",
                        Content = reasoningText
                    });
                }
            }

            var finalContent = contentBuilder.ToString();

            // Native tool calls execution
            if (toolCallsByIndex.Count > 0)
            {
                var executedToolCalls = new List<OpenAiToolCall>();
                var toolResults = new List<(string CallId, string Output)>();

                foreach (var kvp in toolCallsByIndex)
                {
                    var callId = kvp.Value.Id ?? $"call_{Guid.NewGuid():N}";
                    var toolName = kvp.Value.Name.ToString().Trim();
                    var toolArgs = kvp.Value.Args.ToString().Trim();

                    executedToolCalls.Add(new OpenAiToolCall
                    {
                        Id = callId,
                        Type = "function",
                        Function = new OpenAiFunctionCall { Name = toolName, Arguments = toolArgs }
                    });

                    // Check for ask_question
                    if (string.Equals(toolName, "ask_question", StringComparison.OrdinalIgnoreCase))
                    {
                        askedQuestion = true;
                        responseEmitted = true;
                        if (!string.IsNullOrWhiteSpace(finalContent))
                        {
                            await onEvent(new TrajectoryEventPayload
                            {
                                EventType = "response",
                                Content = finalContent
                            });
                        }
                        await onEvent(new TrajectoryEventPayload
                        {
                            EventType = "tool_call",
                            ToolName = "ask_question",
                            Content = "Asking question",
                            ToolArgsJson = toolArgs
                        });
                        break;
                    }

                    // Normal tool execution
                    var summary = FormatToolSummary(toolName, toolArgs);
                    await onEvent(new TrajectoryEventPayload
                    {
                        EventType = "tool_call",
                        ToolName = toolName,
                        Content = summary,
                        ToolArgsJson = toolArgs
                    });

                    // Command budget, not the HTTP handshake budget: ResolveTimeoutSeconds falls back to
                    // 600s, which would mean a wedged test runner holds a worker for ten minutes and the
                    // 180s sandbox default never applies.
                    var toolTimeout = ResolveCommandTimeoutSeconds(preset);
                    var toolResult = await ExecuteToolLocallyAsync(
                        toolName,
                        toolArgs,
                        cwd,
                        ct,
                        toolTimeout,
                        promptEvent.SessionId,
                        callId,
                        onOutputChunk: async (chunk) =>
                        {
                            if (CommandOutputBroadcaster != null)
                            {
                                try { await CommandOutputBroadcaster(promptEvent.SessionId, callId, chunk); } catch { }
                            }
                        });
                    ct.ThrowIfCancellationRequested();

                    await onEvent(new TrajectoryEventPayload
                    {
                        EventType = "tool_result",
                        ToolName = toolName,
                        Content = $"Completed {toolName}",
                        ToolResultJson = toolResult
                    });

                    toolResults.Add((callId, toolResult));
                }

                if (askedQuestion)
                {
                    // Stop loop and wait for human response
                    return;
                }

                // Add assistant response with tool_calls and corresponding tool output messages
                history.Add(new OpenAiMessage
                {
                    Role = "assistant",
                    Content = string.IsNullOrWhiteSpace(finalContent) ? null : finalContent,
                    ToolCalls = executedToolCalls
                });

                foreach (var (cId, output) in toolResults)
                {
                    history.Add(new OpenAiMessage
                    {
                        Role = "tool",
                        ToolCallId = cId,
                        Content = output
                    });
                }
                SaveHistoryToDisk(promptEvent.SessionId, history);

                // Continue loop for next turn with tool results
                continue;
            }

            // Check for fallback tool calls or questions inside content
            var (hasFallbackTool, fbName, fbArgs) = TryParseFallbackToolCall(finalContent);
            if (hasFallbackTool && !string.IsNullOrWhiteSpace(fbName))
            {
                if (string.Equals(fbName, "ask_question", StringComparison.OrdinalIgnoreCase))
                {
                    askedQuestion = true;
                    responseEmitted = true;
                    await onEvent(new TrajectoryEventPayload
                    {
                        EventType = "tool_call",
                        ToolName = "ask_question",
                        Content = "Asking question",
                        ToolArgsJson = fbArgs
                    });
                    return;
                }

                var summary = FormatToolSummary(fbName, fbArgs ?? "{}");
                await onEvent(new TrajectoryEventPayload
                {
                    EventType = "tool_call",
                    ToolName = fbName,
                    Content = summary,
                    ToolArgsJson = fbArgs
                });

                // Command budget, not the HTTP handshake budget: ResolveTimeoutSeconds falls back to 600s,
                // which would mean a wedged test runner holds a worker for ten minutes and the 180s sandbox
                // default never applies.
                var toolTimeout = ResolveCommandTimeoutSeconds(preset);
                var toolResult = await ExecuteToolLocallyAsync(
                    fbName,
                    fbArgs ?? "{}",
                    cwd,
                    ct,
                    toolTimeout,
                    promptEvent.SessionId,
                    "fallback-command",
                    onOutputChunk: async (chunk) =>
                    {
                        if (CommandOutputBroadcaster != null)
                        {
                            try { await CommandOutputBroadcaster(promptEvent.SessionId, "fallback-command", chunk); } catch { }
                        }
                    });

                await onEvent(new TrajectoryEventPayload
                {
                    EventType = "tool_result",
                    ToolName = fbName,
                    Content = $"Completed {fbName}",
                    ToolResultJson = toolResult
                });

                history.Add(new OpenAiMessage { Role = "assistant", Content = finalContent });
                history.Add(new OpenAiMessage { Role = "user", Content = $"Tool Result for {fbName}:\n{toolResult}" });
                SaveHistoryToDisk(promptEvent.SessionId, history);
                continue;
            }

            // Check if the assistant asked numbered or markdown questions without a tool call
            var extractedQuestions = AgentAdapterFactory.ExtractQuestionsFromOutput(finalContent);
            if (extractedQuestions.Count > 0)
            {
                askedQuestion = true;
                responseEmitted = true;
                var qJson = JsonSerializer.Serialize(new
                {
                    questions = extractedQuestions.Select(q => new
                    {
                        question = q.Question,
                        options = q.Options,
                        is_multi_select = q.IsMultiSelect
                    })
                });

                await onEvent(new TrajectoryEventPayload
                {
                    EventType = "tool_call",
                    ToolName = "ask_question",
                    Content = "Asking question",
                    ToolArgsJson = qJson
                });
                return;
            }

            // No tool calls - final assistant response
            var effectiveContent = !string.IsNullOrWhiteSpace(finalContent)
                ? finalContent
                : (reasoningBuilder.Length > 0 ? reasoningBuilder.ToString().Trim() : "Turn completed.");

            history.Add(new OpenAiMessage { Role = "assistant", Content = effectiveContent });

            sw.Stop();
            var duration = sw.Elapsed.TotalSeconds;
            string? structuredJson = null;
            if (totalTokens > 0 || duration > 0)
            {
                structuredJson = JsonSerializer.Serialize(new
                {
                    model = effectiveModel,
                    effort = promptEvent.Effort,
                    mode = promptEvent.Mode,
                    contextSize = promptEvent.ContextSize,
                    usage = new
                    {
                        inputTokens = totalInputTokens,
                        outputTokens = totalOutputTokens,
                        totalTokens = totalTokens > 0 ? totalTokens : totalInputTokens + totalOutputTokens,
                        durationSeconds = duration
                    }
                });
            }

            responseEmitted = true;
            await onEvent(new TrajectoryEventPayload
            {
                EventType = "response",
                Content = effectiveContent,
                StructuredPayloadJson = structuredJson
            });

            break;
        }

        if (!responseEmitted && !askedQuestion)
        {
            await onEvent(new TrajectoryEventPayload
            {
                EventType = "thought",
                Content = $"[@{preset.Id}] Reached maximum tool execution steps limit ({maxSteps}). Requesting final summary..."
            });

            string? summaryContent = null;
            try
            {
                var summaryHistory = new List<OpenAiMessage>(history)
                {
                    new OpenAiMessage
                    {
                        Role = "user",
                        Content = $"[System Notice: You have reached the maximum allowed tool execution steps limit ({maxSteps}) for this turn. Please summarize what you have accomplished so far, the current status of your changes, and what next steps remain so the operator can review and continue.]"
                    }
                };

                var summaryReqBody = new Dictionary<string, object?>
                {
                    ["model"] = effectiveModel,
                    ["messages"] = summaryHistory.Select(m => m.ToSerializableObject()).ToList(),
                    ["stream"] = false
                };

                using var sumHttpReq = new HttpRequestMessage(HttpMethod.Post, chatUrl);
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    sumHttpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                }
                if (preset.CustomHeaders != null)
                {
                    foreach (var kvp in preset.CustomHeaders)
                    {
                        sumHttpReq.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
                    }
                }
                sumHttpReq.Content = new StringContent(JsonSerializer.Serialize(summaryReqBody), Encoding.UTF8, "application/json");

                using var sumCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(120, timeoutSeconds)));
                using var sumLinked = CancellationTokenSource.CreateLinkedTokenSource(ct, sumCts.Token);

                using var sumRes = await HttpClientInstance.SendAsync(sumHttpReq, sumLinked.Token);
                if (sumRes.IsSuccessStatusCode)
                {
                    var sumJson = await sumRes.Content.ReadAsStringAsync(sumLinked.Token);
                    using var sumDoc = JsonDocument.Parse(sumJson);
                    if (sumDoc.RootElement.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0)
                    {
                        var choice = ch[0];
                        if (choice.TryGetProperty("message", out var msgElem) &&
                            msgElem.TryGetProperty("content", out var cVal) &&
                            cVal.ValueKind == JsonValueKind.String)
                        {
                            summaryContent = cVal.GetString();
                        }
                    }
                }
            }
            catch
            {
                // Fall back to canned summary if completion call fails
            }

            var finalMsg = !string.IsNullOrWhiteSpace(summaryContent)
                ? summaryContent.Trim()
                : $"⚠️ Agent `@{preset.Id}` reached maximum tool execution steps limit ({maxSteps}). Current progress has been preserved in workspace. Please reply with instructions or 'continue' to resume.";

            sw.Stop();
            var duration = sw.Elapsed.TotalSeconds;
            string? structuredJson = null;
            if (totalTokens > 0 || duration > 0)
            {
                structuredJson = JsonSerializer.Serialize(new
                {
                    model = effectiveModel,
                    effort = promptEvent.Effort,
                    mode = promptEvent.Mode,
                    contextSize = promptEvent.ContextSize,
                    usage = new
                    {
                        inputTokens = totalInputTokens,
                        outputTokens = totalOutputTokens,
                        totalTokens = totalTokens > 0 ? totalTokens : totalInputTokens + totalOutputTokens,
                        durationSeconds = duration
                    }
                });
            }

            history.Add(new OpenAiMessage { Role = "assistant", Content = finalMsg });
            SaveHistoryToDisk(promptEvent.SessionId, history);

            await onEvent(new TrajectoryEventPayload
            {
                EventType = "response",
                Content = finalMsg,
                StructuredPayloadJson = structuredJson
            });
        }
    }

    private static string BuildSystemPrompt(AgentPresetConfig preset, PromptReceivedEvent promptEvent, string cwd)
    {
        var sb = new StringBuilder();
        var displayName = !string.IsNullOrWhiteSpace(promptEvent.PersonaName)
            ? promptEvent.PersonaName
            : (!string.IsNullOrWhiteSpace(preset.Name) ? preset.Name : preset.Id);
        var role = !string.IsNullOrWhiteSpace(promptEvent.PersonaRole)
            ? promptEvent.PersonaRole
            : preset.Role;

        sb.AppendLine($"You are \"{displayName}\" ({role}), an autonomous AI engineering agent connected via AgentMesh.");
        sb.AppendLine($"Workspace directory: {cwd}");

        if (!string.IsNullOrWhiteSpace(promptEvent.PersonaPrompt))
        {
            sb.AppendLine();
            sb.AppendLine("[Persona Instructions]");
            sb.AppendLine(promptEvent.PersonaPrompt.Trim());
        }

        if (!string.IsNullOrWhiteSpace(promptEvent.Mode))
        {
            sb.AppendLine();
            sb.AppendLine($"[Execution Mode: {promptEvent.Mode}]");
            if (string.Equals(promptEvent.Mode, "plan", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("You are in PLAN mode. Explore the codebase and formulate a comprehensive implementation plan. Do NOT write or edit project files.");
            }
        }

        sb.AppendLine();
        sb.AppendLine("[Autonomous Tool Usage Guidelines]");
        sb.AppendLine("You have access to autonomous tools to execute shell commands, read files, write files, and edit files in the workspace.");
        sb.AppendLine("- Use `run_command` to inspect the project, run tests, execute builds, and check git status.");
        sb.AppendLine("- Use `read_file` to inspect code and text files.");
        sb.AppendLine("- Use `write_file` or `edit_file` to implement solutions.");
        sb.AppendLine("- When you need user clarification or critical architectural decisions, call `ask_question`.");
        sb.AppendLine("- Vision / Image Handling: Attached image files are provided directly to your multimodal visual input when supported. You can inspect, analyze, and describe them visually. However, binary image files on disk cannot be read as plain text via `read_file`. Do NOT attempt to install OCR packages (e.g., tesseract), write custom Python decoders, or parse raw image bytes with shell commands.");
        sb.AppendLine("- Be concise and lead with working code and clear implementation.");

        return sb.ToString();
    }

    public static List<object> GetToolDefinitions()
    {
        return new List<object>
        {
            new
            {
                type = "function",
                function = new
                {
                    name = "run_command",
                    description = "Execute a shell command in the workspace directory. Use this to run builds, tests, git commands, and inspection tools. Long output is preserved in full on disk and returned as a head/tail preview; the result names the log file to grep.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            command = new { type = "string", description = "The exact shell command line string to execute." },
                            workingDirectory = new { type = "string", description = "Optional subdirectory or working directory. Defaults to workspace root." },
                            timeout_seconds = new { type = "integer", description = "Optional wall-clock limit in seconds (5-600, default 180). Use a larger value for full builds or test suites; the command's whole process tree is killed if it exceeds the limit." }
                        },
                        required = new[] { "command" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "read_file",
                    description = "Read the contents of a text file from the workspace.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "The file path relative to workspace root or absolute path." },
                            startLine = new { type = "integer", description = "Optional 1-based start line." },
                            endLine = new { type = "integer", description = "Optional 1-based end line." }
                        },
                        required = new[] { "path" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "write_file",
                    description = "Create a new file or completely overwrite an existing file with the given content.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "The file path relative to workspace root or absolute path." },
                            content = new { type = "string", description = "The full content to write to the file." }
                        },
                        required = new[] { "path", "content" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "edit_file",
                    description = "Replace a target string within an existing file with replacement text.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "The file path relative to workspace root or absolute path." },
                            target = new { type = "string", description = "The exact character sequence in the file to be replaced." },
                            replacement = new { type = "string", description = "The text to replace target with." }
                        },
                        required = new[] { "path", "target", "replacement" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "ask_question",
                    description = "Ask the human operator questions when you need clarification, decision input, or alignment before proceeding.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            questions = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        question = new { type = "string", description = "The question to ask." },
                                        options = new { type = "array", items = new { type = "string" }, description = "List of options for user to select from." },
                                        is_multi_select = new { type = "boolean", description = "Whether user can select multiple choices." }
                                    },
                                    required = new[] { "question" }
                                }
                            }
                        },
                        required = new[] { "questions" }
                    }
                }
            }
        };
    }

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".bmp", ".tiff", ".tif", ".svg"
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".bmp", ".tiff", ".tif",
        ".pdf", ".zip", ".tar", ".gz", ".tgz", ".7z", ".rar", ".bz2", ".xz",
        ".exe", ".dll", ".so", ".dylib", ".bin", ".iso", ".dmg",
        ".mp3", ".mp4", ".wav", ".ogg", ".avi", ".mov", ".webm", ".mkv",
        ".wasm", ".pyc", ".class", ".o", ".obj"
    };

    private static async Task<bool> IsBinaryFileAsync(string filePath, CancellationToken ct)
    {
        var ext = Path.GetExtension(filePath);
        if (BinaryExtensions.Contains(ext))
        {
            return true;
        }

        try
        {
            const int bufferSize = 8192;
            var buffer = new byte[bufferSize];
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), ct);
            for (var i = 0; i < bytesRead; i++)
            {
                if (buffer[i] == 0)
                {
                    return true;
                }
            }
        }
        catch
        {
            // If checking fails, proceed to normal read
        }

        return false;
    }

    public static async Task<string> ExecuteToolLocallyAsync(
        string toolName,
        string argsJson,
        string cwd,
        CancellationToken ct,
        int? timeoutSeconds = null,
        Guid? sessionId = null,
        string? commandId = null,
        Func<string, Task>? onOutputChunk = null)
    {
        var rawResult = await ExecuteToolLocallyRawAsync(toolName, argsJson, cwd, ct, timeoutSeconds, sessionId, commandId, onOutputChunk);
        var truncated = TruncateToolResult(rawResult);
        return truncated.IndexOf('\0') >= 0 ? truncated.Replace("\0", string.Empty) : truncated;
    }

    private static async Task<string> ExecuteToolLocallyRawAsync(
        string toolName,
        string argsJson,
        string cwd,
        CancellationToken ct,
        int? timeoutSeconds = null,
        Guid? sessionId = null,
        string? commandId = null,
        Func<string, Task>? onOutputChunk = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            var root = doc.RootElement;

            switch (toolName.ToLowerInvariant())
            {
                case "run_command":
                case "bash":
                case "command":
                {
                    var cmd = GetPropertyString(root, "command", "CommandLine", "cmd");
                    if (string.IsNullOrWhiteSpace(cmd)) return "Error: 'command' argument is required.";

                    var workDir = GetPropertyString(root, "workingDirectory", "cwd", "workDir");
                    var effectiveDir = !string.IsNullOrWhiteSpace(workDir)
                        ? (Path.IsPathRooted(workDir) ? workDir : Path.Combine(cwd, workDir))
                        : cwd;

                    if (!Directory.Exists(effectiveDir))
                    {
                        Directory.CreateDirectory(effectiveDir);
                    }

                    // Timeout precedence, most specific first:
                    //   1. `timeout_seconds` on the tool call (the model knows whether it is running `echo`
                    //      or a full solution build).
                    //   2. An operator's explicit preset configuration, threaded through `timeoutSeconds`.
                    //   3. null -> the policy default (180s) inside SandboxShell.
                    // Deliberately not ResolveTimeoutSeconds(preset): that is the HTTP handshake budget and
                    // falls back to 600, which would defeat the sandbox default entirely.
                    var requestedTimeout = GetPropertyInt(root, "timeout_seconds", "timeoutSeconds", "timeout")
                        ?? timeoutSeconds;

                    var result = await AgentAdapterFactory.RunCommandAsync(
                        cmd,
                        effectiveDir,
                        ct,
                        timeout: null,
                        sessionId: sessionId,
                        commandId: commandId,
                        onOutputChunk: onOutputChunk,
                        timeoutSeconds: requestedTimeout);

                    var sb = new StringBuilder();
                    if (!string.IsNullOrWhiteSpace(result.Output)) sb.AppendLine(result.Output);

                    // The exit code line is what the adapters and existing tests key on. For a timeout the
                    // diagnostic already names 124; repeating it keeps one source of truth for the number.
                    sb.Append($"Exit Code: {result.ExitCode}");

                    if (result.LogFilePath != null && !result.Output.Contains(result.LogFilePath, StringComparison.Ordinal))
                    {
                        sb.AppendLine();
                        sb.Append($"Full output: {result.LogFilePath}");
                    }

                    return sb.ToString().Trim();
                }

                case "read_file":
                case "view_file":
                {
                    var path = GetPropertyString(root, "path", "Path", "file_path", "filePath");
                    if (string.IsNullOrWhiteSpace(path)) return "Error: 'path' argument is required.";

                    var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(cwd, path);
                    if (!File.Exists(fullPath)) return $"Error: File '{path}' does not exist.";

                    if (await IsBinaryFileAsync(fullPath, ct))
                    {
                        var ext = Path.GetExtension(fullPath);
                        var fi = new FileInfo(fullPath);
                        if (ImageExtensions.Contains(ext) && !string.Equals(ext, ".svg", StringComparison.OrdinalIgnoreCase))
                        {
                            return $"[Image file: '{path}' ({fi.Length:N0} bytes, format: {ext.TrimStart('.').ToUpperInvariant()}). This is a binary image file and cannot be read as plain text. Attached images are already provided directly to your multimodal visual context in the prompt. Do NOT attempt to install OCR packages (e.g., tesseract), write custom Python decoders, or parse raw image bytes with shell commands. If you need details about an unattached image, ask the user to attach or describe it.]";
                        }
                        return $"[Binary file: '{path}' ({fi.Length:N0} bytes). This is a binary file and cannot be read as plain text.]";
                    }

                    var lines = await File.ReadAllLinesAsync(fullPath, ct);
                    int startLine = GetPropertyInt(root, "startLine", "StartLine") ?? 1;
                    int endLine = GetPropertyInt(root, "endLine", "EndLine") ?? lines.Length;

                    startLine = Math.Max(1, startLine);
                    endLine = Math.Min(lines.Length, Math.Max(startLine, endLine));

                    var sb = new StringBuilder();
                    for (int i = startLine - 1; i < endLine; i++)
                    {
                        sb.AppendLine($"{i + 1}: {lines[i]}");
                    }
                    return sb.ToString().TrimEnd();
                }

                case "write_file":
                {
                    var path = GetPropertyString(root, "path", "Path", "file_path", "filePath");
                    if (string.IsNullOrWhiteSpace(path)) return "Error: 'path' argument is required.";

                    var content = GetPropertyString(root, "content", "Content", "text");
                    content ??= string.Empty;

                    var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(cwd, path);
                    var dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    await File.WriteAllTextAsync(fullPath, content, Encoding.UTF8, ct);
                    return $"Successfully wrote {content.Length} characters to '{path}'.";
                }

                case "edit_file":
                case "replace_file_content":
                {
                    var path = GetPropertyString(root, "path", "Path", "file_path", "filePath");
                    if (string.IsNullOrWhiteSpace(path)) return "Error: 'path' argument is required.";

                    var target = GetPropertyString(root, "target", "Target", "old_string", "oldText");
                    var replacement = GetPropertyString(root, "replacement", "Replacement", "new_string", "newText") ?? string.Empty;

                    if (string.IsNullOrEmpty(target)) return "Error: 'target' argument is required.";

                    var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(cwd, path);
                    if (!File.Exists(fullPath)) return $"Error: File '{path}' does not exist.";

                    var fileText = await File.ReadAllTextAsync(fullPath, ct);
                    if (!fileText.Contains(target))
                    {
                        return $"Error: Target string not found in '{path}'.";
                    }

                    var updatedText = fileText.Replace(target, replacement);
                    await File.WriteAllTextAsync(fullPath, updatedText, Encoding.UTF8, ct);
                    return $"Successfully replaced target in '{path}'.";
                }

                default:
                    return $"Error: Unknown tool '{toolName}'.";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Tool execution error: {ex.Message}";
        }
    }

    private static string FormatToolSummary(string toolName, string argsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            var root = doc.RootElement;
            switch (toolName.ToLowerInvariant())
            {
                case "run_command":
                case "bash":
                    var cmd = GetPropertyString(root, "command", "CommandLine", "cmd");
                    return !string.IsNullOrWhiteSpace(cmd) ? $"Running command: {cmd}" : "Running command";
                case "read_file":
                    var rPath = GetPropertyString(root, "path", "Path", "file_path");
                    return !string.IsNullOrWhiteSpace(rPath) ? $"Reading file: {Path.GetFileName(rPath)}" : "Reading file";
                case "write_file":
                    var wPath = GetPropertyString(root, "path", "Path", "file_path");
                    return !string.IsNullOrWhiteSpace(wPath) ? $"Writing file: {Path.GetFileName(wPath)}" : "Writing file";
                case "edit_file":
                    var ePath = GetPropertyString(root, "path", "Path", "file_path");
                    return !string.IsNullOrWhiteSpace(ePath) ? $"Editing file: {Path.GetFileName(ePath)}" : "Editing file";
                case "ask_question":
                    return "Asking question";
                default:
                    return $"Executing {toolName}";
            }
        }
        catch
        {
            return $"Executing {toolName}";
        }
    }

    private static (bool HasTool, string? Name, string? Args) TryParseFallbackToolCall(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return (false, null, null);

        // Check for <tool_call> JSON block
        var xmlMatch = Regex.Match(content, @"<tool_call>\s*([\s\S]*?)\s*</tool_call>", RegexOptions.IgnoreCase);
        if (xmlMatch.Success)
        {
            try
            {
                using var doc = JsonDocument.Parse(xmlMatch.Groups[1].Value);
                var root = doc.RootElement;
                var name = GetPropertyString(root, "name", "tool", "function");
                var args = root.TryGetProperty("arguments", out var aProp)
                    ? (aProp.ValueKind == JsonValueKind.String ? aProp.GetString() : aProp.GetRawText())
                    : (root.TryGetProperty("parameters", out var pProp) ? pProp.GetRawText() : "{}");
                return (true, name, args);
            }
            catch { }
        }

        // Check for ```tool_call block
        var mdMatch = Regex.Match(content, @"```(?:json:)?tool_call\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
        if (mdMatch.Success)
        {
            try
            {
                using var doc = JsonDocument.Parse(mdMatch.Groups[1].Value);
                var root = doc.RootElement;
                var name = GetPropertyString(root, "name", "tool", "function");
                var args = root.TryGetProperty("arguments", out var aProp)
                    ? (aProp.ValueKind == JsonValueKind.String ? aProp.GetString() : aProp.GetRawText())
                    : (root.TryGetProperty("parameters", out var pProp) ? pProp.GetRawText() : "{}");
                return (true, name, args);
            }
            catch { }
        }

        // Check for ```ask_question block
        var qMatch = Regex.Match(content, @"```(?:json:)?ask_question\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
        if (qMatch.Success)
        {
            return (true, "ask_question", qMatch.Groups[1].Value);
        }

        return (false, null, null);
    }

    private static string? GetPropertyString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.String) return prop.GetString();
                if (prop.ValueKind != JsonValueKind.Null && prop.ValueKind != JsonValueKind.Undefined) return prop.GetRawText();
            }
        }
        return null;
    }

    private static int? GetPropertyInt(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var prop) && prop.TryGetInt32(out var val))
            {
                return val;
            }
        }
        return null;
    }
}

public class OpenAiMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }

    [JsonIgnore]
    public List<object>? ContentParts { get; set; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OpenAiToolCall>? ToolCalls { get; set; }

    /// <summary>
    /// Shallow copy for the dispatch snapshot.
    /// </summary>
    /// <remarks>
    /// <see cref="ContentParts"/> is shared by reference rather than deep-copied: the guard only ever
    /// rewrites <see cref="Content"/> on the copy, and the parts list is built once per multimodal turn
    /// and never mutated afterwards. Deep-copying a base64 image payload on every retry would be the
    /// most expensive line in the file to add for a defensive path that never runs.
    /// </remarks>
    public OpenAiMessage Clone() => new()
    {
        Role = Role,
        Content = Content,
        ContentParts = ContentParts,
        ToolCallId = ToolCallId,
        ToolCalls = ToolCalls,
    };

    public object ToSerializableObject()
    {
        var dict = new Dictionary<string, object?> { ["role"] = Role };
        if (ContentParts != null && ContentParts.Count > 0)
        {
            dict["content"] = ContentParts;
        }
        else if (Content != null)
        {
            dict["content"] = Content;
        }
        if (!string.IsNullOrEmpty(ToolCallId)) dict["tool_call_id"] = ToolCallId;
        if (ToolCalls != null && ToolCalls.Count > 0)
        {
            dict["tool_calls"] = ToolCalls.Select(tc => new
            {
                id = tc.Id,
                type = tc.Type,
                function = new
                {
                    name = tc.Function.Name,
                    arguments = tc.Function.Arguments
                }
            }).ToList();
        }
        return dict;
    }
}

public class OpenAiTextContentPart
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

public class OpenAiImageUrlContentPart
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "image_url";

    [JsonPropertyName("image_url")]
    public OpenAiImageUrl ImageUrl { get; set; } = new();
}

public class OpenAiImageUrl
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

public class OpenAiToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public OpenAiFunctionCall Function { get; set; } = new();
}

public class OpenAiFunctionCall
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;
}
