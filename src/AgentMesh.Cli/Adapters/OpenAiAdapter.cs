using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentMesh.Cli.Models;
using AgentMesh.Cli.Services;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;
using ModelContextProtocol.Client;

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

    public static Func<Guid, string, string, Task>? CommandOutputBroadcaster
    {
        get => AgentToolExecutor.CommandOutputBroadcaster;
        set => AgentToolExecutor.CommandOutputBroadcaster = value;
    }

    /// <summary>
    /// Optional callback invoked when a context-window overflow error reveals a model's true token limit.
    /// Args: (driverId, modelId, maxContextTokens).
    /// </summary>
    public static Func<string, string, int, Task>? LearnedContextLimitReporter { get; set; }

    /// <summary>
    /// Optional callback invoked when the agent calls a platform tool (manage_task, post_job_message, open_pull_request, save_memory).
    /// Args: (toolName, argsJson, sessionId, personaId, agentId, ct).
    /// </summary>
    public static Func<string, string, Guid?, string?, string?, CancellationToken, Task<string>>? PlatformToolExecutor
    {
        get => AgentToolExecutor.PlatformToolExecutor;
        set => AgentToolExecutor.PlatformToolExecutor = value;
    }

    internal static Func<ResolvedMcpServerConfig, string?, IClientTransport?>? McpTransportFactoryForTest
    {
        get => AgentToolExecutor.McpTransportFactoryForTest;
        set => AgentToolExecutor.McpTransportFactoryForTest = value;
    }

    internal static void ResetMcpTransportFactoryForTest()
        => AgentToolExecutor.ResetMcpTransportFactoryForTest();

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
        => AgentToolExecutor.ResolveCommandTimeoutSeconds(preset);

    public static int ResolveMaxRetries(AgentPresetConfig preset)
    {
        if (preset.MaxRetries.HasValue && preset.MaxRetries.Value >= 0)
            return preset.MaxRetries.Value;

        if (int.TryParse(Environment.GetEnvironmentVariable("OPENAI_MAX_RETRIES"), out var envRetries) && envRetries >= 0)
            return envRetries;

        return 2; // 2 retries (3 total attempts)
    }

    public static int ResolveMaxSteps(AgentPresetConfig preset, bool isDiscoveryPhase = false, int? globalDefault = null)
    {
        if (preset.MaxSteps.HasValue && preset.MaxSteps.Value > 0)
            return preset.MaxSteps.Value;

        if (globalDefault.HasValue && globalDefault.Value > 0)
            return globalDefault.Value;

        if (int.TryParse(Environment.GetEnvironmentVariable("AGENTMESH_MAX_STEPS"), out var meshSteps) && meshSteps > 0)
            return meshSteps;

        if (int.TryParse(Environment.GetEnvironmentVariable("OPENAI_MAX_STEPS"), out var envSteps) && envSteps > 0)
            return envSteps;

        if (int.TryParse(Environment.GetEnvironmentVariable("AGENT_MAX_STEPS"), out var agentSteps) && agentSteps > 0)
            return agentSteps;

        return 50;
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
    /// Resolves the token budget for a run, respecting explicit options and agent/driver profile
    /// context sizes (including 512k / 524,288) without clamping to legacy 131,072 tables.
    /// </summary>
    public static int ResolveModelContextLimit(string model, string? contextSizeOption, AgentPresetConfig? preset = null)
    {
        if (ModelContextLimits.ParseOption(contextSizeOption) is int explicitTokens && explicitTokens > 0)
        {
            return explicitTokens;
        }

        if (preset?.ContextWindowTokens is int presetTokens && presetTokens > 0)
        {
            return presetTokens;
        }

        if (preset?.OpenAi?.ContextWindowTokens is int settingsTokens && settingsTokens > 0)
        {
            return settingsTokens;
        }

        if (ModelContextLimits.ParseOption(preset?.DefaultContextSize) is int presetOptionTokens && presetOptionTokens > 0)
        {
            return presetOptionTokens;
        }

        if (ModelContextLimits.ParseOption(preset?.OpenAi?.DefaultContextSize) is int settingsOptionTokens && settingsOptionTokens > 0)
        {
            return settingsOptionTokens;
        }

        var envWindow = Environment.GetEnvironmentVariable("OPENAI_CONTEXT_WINDOW")
                        ?? Environment.GetEnvironmentVariable("OPENAI_MAX_CONTEXT_TOKENS");
        if (ModelContextLimits.ParseOption(envWindow) is int envTokens && envTokens > 0)
        {
            return envTokens;
        }

        return ModelContextLimits.ResolveTokens(model, contextSizeOption);
    }

    /// <summary>
    /// Resolves the dynamic compaction threshold percentage (default 75%).
    /// </summary>
    public static int ResolveCompactionThresholdPercent(AgentPresetConfig? preset = null)
    {
        if (preset?.CompactionThresholdPercent is int configured && configured > 0)
        {
            return Math.Clamp(configured, 10, 95);
        }

        if (preset?.OpenAi?.CompactionThresholdPercent is int fromSettings && fromSettings > 0)
        {
            return Math.Clamp(fromSettings, 10, 95);
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("OPENAI_COMPACTION_THRESHOLD_PERCENT"), out var envPct) && envPct > 0)
        {
            return Math.Clamp(envPct, 10, 95);
        }

        return OpenAISettings.DefaultCompactionThresholdPercent;
    }

    /// <summary>
    /// Resolves the post-first-byte SSE chunk idle timeout in seconds (default 120s).
    /// </summary>
    public static int ResolveStreamChunkIdleTimeoutSeconds(AgentPresetConfig? preset = null)
    {
        if (preset?.StreamChunkIdleTimeoutSeconds is int configured && configured > 0)
        {
            return configured;
        }

        if (preset?.OpenAi?.StreamChunkIdleTimeoutSeconds is int fromSettings && fromSettings > 0)
        {
            return fromSettings;
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("OPENAI_STREAM_CHUNK_IDLE_TIMEOUT_SECONDS"), out var envSec) && envSec > 0)
        {
            return envSec;
        }

        return OpenAISettings.DefaultStreamChunkIdleTimeoutSeconds;
    }

    /// <summary>
    /// Per-turn budget for waiting out model-server swaps/reloads. Single-residency swap servers
    /// (llama-swap class deployments: one model resident, picks evict it) report offline for the
    /// minutes a multi-shard reload takes — the plain 3-retries-in-7-seconds policy turned every
    /// swap into a dead job. 0 disables the extended wait and keeps the old behavior.
    /// </summary>
    public static int ResolveSwapWaitBudgetSeconds(AgentPresetConfig preset)
    {
        if (preset?.SwapWaitSeconds is int configured && configured >= 0) return configured;
        if (int.TryParse(Environment.GetEnvironmentVariable("AGENTMESH_SWAP_WAIT_SECONDS"), out var env) && env >= 0) return env;
        return 600;
    }

    /// <summary>
    /// A 5xx whose body names a model reload, not just any 5xx: a bare "Service Unavailable"
    /// must keep the plain transient-retry behavior, or every ordinary blip parks the job for
    /// the swap budget. Swap/reload servers say so in the body ("(titanium) is offline",
    /// "loading", "warming up") — that self-description is the signal.
    /// </summary>
    internal static bool IsModelServerSwapSignal(int statusCode, string? errorContent) =>
        statusCode >= 500 && statusCode < 600 && !string.IsNullOrEmpty(errorContent) &&
            (errorContent.Contains("offline", StringComparison.OrdinalIgnoreCase)
             || errorContent.Contains("loading", StringComparison.OrdinalIgnoreCase)
             || errorContent.Contains("swapping", StringComparison.OrdinalIgnoreCase)
             || errorContent.Contains("warm", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Conservative detector for "response ended the turn while promising to act" — the visible
    /// half of the dropped-tool-call class (the reasoning/tool parser ate the call and left the
    /// narration). Past-tense summaries ("I ran the tests and they pass") do not match; a
    /// question in the closing stretch does not match either — the question heuristics own those.
    /// </summary>
    internal static bool LooksLikeUnexecutedIntent(string? content)
        => AgentToolExecutor.LooksLikeUnexecutedIntent(content);

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
        // Round-tripped reasoning is billed input on every later turn — compaction must see it.
        charCount += message.ReasoningContent?.Length ?? 0;
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
        => AgentToolExecutor.TruncateToolResult(result, maxChars);

    public async Task ExecutePromptAsync(
        AgentPresetConfig preset,
        PromptReceivedEvent promptEvent,
        Func<TrajectoryEventPayload, Task> onEvent,
        CancellationToken ct)
    {
        var cwd = AgentAdapterFactory.ResolveWorkspaceDirectory(promptEvent);

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
        var modelContextLimit = ResolveModelContextLimit(effectiveModel, promptEvent.ContextSize, preset);
        var compactionThresholdPercent = ResolveCompactionThresholdPercent(preset);
        var streamChunkIdleTimeoutSeconds = ResolveStreamChunkIdleTimeoutSeconds(preset);

        // Request-shaping policy for this model+endpoint (Core ModelBehaviorProfiles): explicit
        // preset override → learned endpoint facts → family table → empty default. An unknown
        // model gets exactly the pre-registry request shape, so adding a registry row can never
        // regress a model that row does not name.
        var isLoopbackEndpoint = endpoint.Contains("localhost") || endpoint.Contains("127.0.0.1") || endpoint.Contains("[::1]");
        var behaviorProfile = ModelBehaviorProfiles.Resolve(
            effectiveModel,
            isLoopbackEndpoint,
            ParseDialectOverride(preset.SamplingDialect),
            preset.Sampling,
            ParseThinkingOverride(preset.ThinkingRoundTrip));

        // Companion pre-flight: fail (or repair) in 20s instead of burning the retry budget — or a
        // 600s prefill timeout — against a daemon that was never listening.
        var (companionReady, companionGuidance) = await EnsureCompanionReadyAsync(preset, endpoint, onEvent, ct);
        if (!companionReady)
        {
            await onEvent(new TrajectoryEventPayload
            {
                EventType = "response",
                Content = $"❌ {companionGuidance}"
            });
            return;
        }

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

        var isPastDiscovery = (promptEvent.PromptStack?.UserTaskEnvelope?.Contains("Plan approved", StringComparison.OrdinalIgnoreCase) == true)
            || (promptEvent.Prompt?.Contains("Plan approved", StringComparison.OrdinalIgnoreCase) == true)
            || (promptEvent.PromptStack?.UserTaskEnvelope?.Contains("Executing task", StringComparison.OrdinalIgnoreCase) == true)
            || (promptEvent.Prompt?.Contains("Executing task", StringComparison.OrdinalIgnoreCase) == true);

        var isDiscoveryPhase = !isPastDiscovery && (
            (promptEvent.PromptStack?.UserTaskEnvelope?.Contains("stage-1-discovery", StringComparison.OrdinalIgnoreCase) == true)
            || (promptEvent.Prompt?.Contains("stage-1-discovery", StringComparison.OrdinalIgnoreCase) == true)
            || (promptEvent.PromptStack?.UserTaskEnvelope?.Contains("[STAGE 1: DISCOVERY", StringComparison.OrdinalIgnoreCase) == true)
            || (promptEvent.PromptStack?.UserTaskEnvelope?.Contains("Awaiting plan approval", StringComparison.OrdinalIgnoreCase) == true)
            || (promptEvent.Prompt?.Contains("Awaiting plan approval", StringComparison.OrdinalIgnoreCase) == true));

        var isDirectMode = string.Equals(promptEvent.Mode, "direct", StringComparison.OrdinalIgnoreCase);

        await using var mcpManager = (!isDirectMode && promptEvent.McpServers is { Count: > 0 })
            ? await ConnectMcpServersAsync(promptEvent.McpServers, preset, cwd, onEvent, ct)
            : null;

        // Tool schema is FROZEN across phases: the definition array sits inside the cached
        // system prefix, so swapping it at every stage transition busts the very prefix the
        // layers-1-5 prompt stack exists to keep warm — and mid-session tool-set churn also
        // confuses models that learned the earlier list. Stage enforcement belongs in the
        // executor (ExecuteToolLocallyRawAsync blocks write/edit/git-mutation/PR during
        // discovery with instructive errors), which is both cache-stable and teaches the model
        // why a call was refused instead of silently hiding the capability.
        var toolsDefinition = isDirectMode
            ? null
            : GetToolDefinitions(mcpManager?.GetOpenAiToolDefinitions());

        // Compaction: summarize older turns with the model (lossy fallback to truncation), then persist.
        async Task<(bool Changed, bool Summarized)> CompactAsync(int target, bool aggressive)
        {
            var summarized = await TrySummarizeOldTurnsAsync(history, aggressive ? 2 : 4, chatUrl, effectiveModel, apiKey, preset, timeoutSeconds, ct);
            var changed = CompactHistory(history, target, aggressive);
            if (changed || summarized) SaveHistoryToDisk(promptEvent.SessionId, history);
            return (changed || summarized, summarized);
        }

        var maxSteps = ResolveMaxSteps(preset, isDiscoveryPhase);
        var stepCount = 0;
        var readOnlyStreak = 0;
        var warnedBudget = false;
        var profilePruneApplied = false;
        var intentNudgesUsed = 0;
        var swapWaitSecondsUsed = 0;
        var swapWaitAttempt = 0;
        var swapWaitBudgetSeconds = ResolveSwapWaitBudgetSeconds(preset);
        // The sampling keys THIS request actually carried — the only names a 400 may learn
        // against; base fields are never prunable.
        var sentSamplingKeys = new List<string>();
        var streamRetryAttempt = 0;
        var responseEmitted = false;
        var askedQuestion = false;
        var filesInspected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filesModified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var commandsRun = new List<string>();
        string? lastActionSummary = null;
        long totalInputTokens = 0;
        long totalOutputTokens = 0;
        long totalTokens = 0;
        var sw = Stopwatch.StartNew();

        while (stepCount < maxSteps)
        {
            ct.ThrowIfCancellationRequested();

            // Proactive history compaction at configured percentage of resolved context window (default 75%)
            var targetMaxTokens = (int)Math.Floor(modelContextLimit * (compactionThresholdPercent / 100.0));
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
                        Content = $"[@{preset.Id}] Proactively {(summarized ? "summarized" : "truncated")} conversation history ({beforeEst} -> {afterEst} tokens, threshold {compactionThresholdPercent}%) to remain within {modelContextLimit} context limit."
                    });
                }
            }

            HttpResponseMessage? httpRes = null;
            CancellationTokenSource? outerRequestCts = null;
            var retryAttempt = 0;
            var contentBuilder = new StringBuilder();
            var reasoningBuilder = new StringBuilder();
            var reasoningShape = new ReasoningShapeTracker();
            var toolCallsByIndex = new SortedDictionary<int, (string? Id, StringBuilder Name, StringBuilder Args)>();
            Exception? streamDropped = null;

            try
            {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                outerRequestCts?.Dispose();
                outerRequestCts = null;

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
                    ["stream"] = true,
                    ["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true }
                };
                // Rebuilt every step, so keys learned-rejected on a 400 moments ago are honored by
                // the next iteration without extra bookkeeping.
                sentSamplingKeys = ApplyProfileSampling(reqBody, behaviorProfile, effectiveModel);

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

                // Outer HTTP timeout covers both initial TCP/TLS handshake AND prompt prefill (TTFT)
                // up until the first SSE data chunk arrives.
                outerRequestCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, outerRequestCts.Token);

                try
                {
                    httpRes = await HttpClientInstance.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && outerRequestCts.IsCancellationRequested)
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
                    // Hint is derived from the driver profile's own companion commands rather than a
                    // named daemon binary; only when the profile declares none do we fall back to
                    // generic proxy advice.
                    var isLocal = endpoint.Contains("localhost") || endpoint.Contains("127.0.0.1");
                    var companionHint = BuildCompanionAuthHint(preset);
                    var extraHint = isLocal
                        ? (companionHint != null
                            ? $"\n\n💡 {companionHint} You can also test and kickstart the companion via 'Verify Driver' or the interactive terminal in the Persona Environment modal."
                            : "\n\n💡 If a driver companion daemon or local proxy fronts this endpoint, ensure it is running and signed in. You can test and kickstart it via 'Verify Driver' or the interactive terminal in the Persona Environment modal.")
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

                    // Profile self-heal: if the endpoint rejected a sampling key we added (or a
                    // round-tripped reasoning field), learn that endpoint fact, prune, and retry
                    // the step once. Only our own additions are prunable — base request fields
                    // are out of reach — and the learning makes every later turn skip what was
                    // rejected instead of re-triggering the 400.
                    if (statusCode == 400 && !profilePruneApplied)
                    {
                        var didPrune = false;
                        foreach (var rejectedKey in ParseRejectedRequestParameters(errContent, sentSamplingKeys))
                        {
                            if (ModelBehaviorProfiles.RecordRejectedSamplingKey(effectiveModel, rejectedKey))
                                didPrune = true;
                        }
                        if (!didPrune &&
                            errContent.Contains("reasoning_content", StringComparison.OrdinalIgnoreCase) &&
                            history.Any(m => m.ReasoningContent != null) &&
                            !ModelBehaviorProfiles.IsReasoningFieldRejected(effectiveModel))
                        {
                            ModelBehaviorProfiles.RecordReasoningFieldRejected(effectiveModel);
                            didPrune = true;
                        }
                        if (didPrune)
                        {
                            profilePruneApplied = true;
                            httpRes.Dispose();
                            await onEvent(new TrajectoryEventPayload
                            {
                                EventType = "thought",
                                Content = $"[@{preset.Id}] [Profile guard] The endpoint rejected part of the request shape for '{effectiveModel}' (unknown sampling parameter or reasoning field). Pruned it and retried the step — later turns will not send it."
                            });
                            continue;
                        }
                    }

                    // Park through model swaps instead of dying on them: on a single-residency
                    // swap server a 503/offline is a minutes-long weight reload, and the plain
                    // retry policy spent its whole budget inside the first seconds of it. Backed
                    // off here, the turn resumes on its own; disk-persisted session state makes
                    // the pause safe. Budget exhausted -> falls through to the normal retry/❌
                    // path unchanged.
                    if (IsModelServerSwapSignal(statusCode, errContent) && swapWaitSecondsUsed < swapWaitBudgetSeconds)
                    {
                        var swapDelaySeconds = Math.Min(60, 3 * (int)Math.Pow(2, swapWaitAttempt));
                        swapWaitAttempt++;
                        swapWaitSecondsUsed += swapDelaySeconds;
                        httpRes.Dispose();
                        await onEvent(new TrajectoryEventPayload
                        {
                            EventType = "thought",
                            Content = $"[@{preset.Id}] [Swap guard] The model server returned HTTP {statusCode} and describes itself as unavailable — likely a swap/reload, which takes minutes. Paused {swapWaitSecondsUsed}s of a {swapWaitBudgetSeconds}s budget this turn; retrying in {swapDelaySeconds}s. The job is parked, not failed."
                        });
                        await Task.Delay(TimeSpan.FromSeconds(swapDelaySeconds), ct);
                        continue;
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
                    var companionAuthHint = statusCode == 401 ? BuildCompanionAuthHint(preset) : null;
                    await onEvent(new TrajectoryEventPayload
                    {
                        EventType = "response",
                        Content = companionAuthHint == null ? $"❌ {errorMsg}" : $"❌ {errorMsg}\n\n{companionAuthHint}"
                    });
                    httpRes.Dispose();
                    return;
                }

                break;
            }

            using var activeResponse = httpRes!;

            var firstChunkReceived = false;
            var chunkIdleTimeout = TimeSpan.FromSeconds(streamChunkIdleTimeoutSeconds);
            using (var prefillLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, outerRequestCts!.Token))
            using (var stream = await activeResponse.Content.ReadAsStreamAsync(prefillLinkedCts.Token))
            using (var reader = new StreamReader(stream))
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    using var chunkIdleCts = firstChunkReceived
                        ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                        : null;
                    chunkIdleCts?.CancelAfter(chunkIdleTimeout);

                    var readToken = firstChunkReceived ? chunkIdleCts!.Token : prefillLinkedCts.Token;

                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(readToken);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested && firstChunkReceived && chunkIdleCts!.IsCancellationRequested)
                    {
                        await onEvent(new TrajectoryEventPayload
                        {
                            EventType = "thought",
                            Content = $"[@{preset.Id}] SSE stream stalled for {streamChunkIdleTimeoutSeconds}s after first chunk; cancelling and retrying..."
                        });
                        streamDropped = new TimeoutException($"SSE stream stalled after {streamChunkIdleTimeoutSeconds}s of chunk inactivity.");
                        break;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested && !firstChunkReceived && outerRequestCts.IsCancellationRequested)
                    {
                        await onEvent(new TrajectoryEventPayload
                        {
                            EventType = "thought",
                            Content = $"[@{preset.Id}] Initial prompt prefill (TTFT) timed out after {timeoutSeconds}s waiting for first SSE chunk."
                        });
                        streamDropped = new TimeoutException($"Prompt prefill timed out after {timeoutSeconds}s before first SSE chunk.");
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
                    if (trimmed.StartsWith('{'))
                    {
                        firstChunkReceived = true;
                        // The server returned a direct JSON body rather than SSE lines
                        using var restCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        restCts.CancelAfter(chunkIdleTimeout);
                        var rest = await reader.ReadToEndAsync(restCts.Token);
                        var fullJson = trimmed + "\n" + rest;
                        try
                        {
                            using var doc = JsonDocument.Parse(fullJson);
                            ParseOpenAiChunkOrMessage(doc.RootElement, contentBuilder, reasoningBuilder, toolCallsByIndex, ref totalInputTokens, ref totalOutputTokens, ref totalTokens, reasoningShape);
                        }
                        catch { }
                        break;
                    }

                    if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

                    var data = trimmed.Length > 5 ? trimmed[5..].Trim() : "";
                    firstChunkReceived = true;
                    if (data == "[DONE]") break;

                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        ParseOpenAiChunkOrMessage(doc.RootElement, contentBuilder, reasoningBuilder, toolCallsByIndex, ref totalInputTokens, ref totalOutputTokens, ref totalTokens, reasoningShape);
                    }
                    catch
                    {
                        // Ignore malformed chunk
                    }
                }
            }
            }
            finally
            {
                outerRequestCts?.Dispose();
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

            var rawFinalContent = contentBuilder.ToString();
            var (inlineThought, cleanedFinalContent) = ExtractInlineThinkBlocks(rawFinalContent);
            if (!string.IsNullOrWhiteSpace(inlineThought))
            {
                if (reasoningBuilder.Length == 0)
                {
                    reasoningBuilder.Append(inlineThought);
                }
                rawFinalContent = cleanedFinalContent;
            }
            var finalContent = rawFinalContent;

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
            else if (toolCallsByIndex.Count > 0 &&
                     !string.IsNullOrWhiteSpace(finalContent) &&
                     !toolCallsByIndex.Values.Any(v => string.Equals(v.Name.ToString().Trim(), "ask_question", StringComparison.OrdinalIgnoreCase)))
            {
                var narrationText = StripRawToolCallTags(finalContent);
                if (!string.IsNullOrWhiteSpace(narrationText))
                {
                    await onEvent(new TrajectoryEventPayload
                    {
                        EventType = "thought",
                        Content = narrationText
                    });
                }
            }

            // Native tool calls execution
            if (toolCallsByIndex.Count > 0)
            {
                var executedToolCalls = new List<OpenAiToolCall>();
                var toolResults = new List<(string CallId, string Output)>();
                var checklistRegistered = false;
                var prOpened = false;

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
                    var summary = FormatToolSummary(toolName, toolArgs, mcpManager);
                    lastActionSummary = summary;
                    TrackToolActivity(toolName, toolArgs, filesInspected, filesModified, commandsRun);
                    await onEvent(new TrajectoryEventPayload
                    {
                        EventType = "tool_call",
                        ToolName = toolName,
                        Content = summary,
                        ToolArgsJson = toolArgs
                    });

                    var toolResult = await ExecuteBridgedOrLocalToolAsync(
                        toolName,
                        toolArgs,
                        cwd,
                        mcpManager,
                        preset,
                        promptEvent,
                        callId,
                        isDiscoveryPhase,
                        ct);
                    ct.ThrowIfCancellationRequested();

                    await onEvent(new TrajectoryEventPayload
                    {
                        EventType = "tool_result",
                        ToolName = toolName,
                        Content = $"Completed {toolName}",
                        ToolResultJson = toolResult
                    });

                    toolResults.Add((callId, toolResult));

                    // post_job_message with requires_reply=true parks the turn
                    if (string.Equals(toolName, "post_job_message", StringComparison.OrdinalIgnoreCase) && IsRequiresReply(toolArgs))
                    {
                        var isError = IsToolResultError(toolResult);
                        if (!isError)
                        {
                            askedQuestion = true;
                            responseEmitted = true;
                            await onEvent(new TrajectoryEventPayload
                            {
                                EventType = "response",
                                Content = "Turn paused: plan / question posted to Job Chat awaiting reply."
                            });
                            break;
                        }
                    }

                    // open_pull_request yields the turn to code review
                    if (string.Equals(toolName, "open_pull_request", StringComparison.OrdinalIgnoreCase))
                    {
                        var isError = IsToolResultError(toolResult);
                        if (!isError)
                        {
                            responseEmitted = true;
                            prOpened = true;
                            break;
                        }
                    }

                    // manage_task with action='set_checklist' registers the living task checklist.
                    // Do not abort immediately: allow subsequent tools in the batch (e.g. post_job_message) to execute.
                    if (string.Equals(toolName, "manage_task", StringComparison.OrdinalIgnoreCase) && IsSetChecklistAction(toolArgs))
                    {
                        var isError = IsToolResultError(toolResult);
                        if (!isError)
                        {
                            checklistRegistered = true;
                        }
                    }
                }

                // Add assistant response with tool_calls and corresponding tool output messages
                var nativeAssistantMessage = new OpenAiMessage
                {
                    Role = "assistant",
                    Content = string.IsNullOrWhiteSpace(finalContent) ? null : finalContent,
                    ToolCalls = executedToolCalls
                };
                MaybeAttachReasoningToHistory(nativeAssistantMessage, reasoningBuilder.ToString(), reasoningShape, behaviorProfile, effectiveModel);
                history.Add(nativeAssistantMessage);

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

                if (prOpened)
                {
                    // The turn must end on a terminal response event. Returning silently made the
                    // runner treat a SUCCESSFUL PR submission as an unterminated turn, and its
                    // safety net then blamed whatever stale tool error happened to be recorded
                    // anywhere in the turn — a PR-opening job filed as ❌ with an unrelated edit
                    // miss in the message.
                    await onEvent(new TrajectoryEventPayload
                    {
                        EventType = "response",
                        Content = (string.IsNullOrWhiteSpace(finalContent) ? "" : finalContent.Trim() + "\n\n")
                            + "Pull request registered for this branch — turn yielded to the review gate."
                    });
                    return;
                }

                if (askedQuestion)
                {
                    // Stop loop and wait for human response
                    return;
                }

                if (checklistRegistered && !askedQuestion)
                {
                    responseEmitted = true;
                    await onEvent(new TrajectoryEventPayload
                    {
                        EventType = "response",
                        Content = !string.IsNullOrWhiteSpace(finalContent)
                            ? $"{finalContent.Trim()}\n\nTurn concluded: Living task checklist registered. Awaiting Stage 2 Plan Approval."
                            : "Turn concluded: Living task checklist registered. Awaiting Stage 2 Plan Approval."
                    });
                    return;
                }

                // Differentiated step budget: pure read-only turns increment step budget every 2 turns,
                // while mutating commands increment every turn.
                bool isPureReadOnlyTurn = executedToolCalls.All(c => IsReadOnlyTool(c.Function.Name, c.Function.Arguments, mcpManager));
                if (isPureReadOnlyTurn)
                {
                    readOnlyStreak++;
                    if (readOnlyStreak % 2 == 0)
                    {
                        stepCount++;
                    }
                }
                else
                {
                    readOnlyStreak = 0;
                    stepCount++;
                }

                if (!warnedBudget && stepCount >= (int)(maxSteps * 0.8) && maxSteps > 5)
                {
                    warnedBudget = true;
                    await onEvent(new TrajectoryEventPayload
                    {
                        EventType = "thought",
                        Content = $"[@{preset.Id}] [Turn Step Budget Notice] Approaching step budget ({stepCount}/{maxSteps}). Please wrap up current verification, update task status via manage_task, and conclude turn."
                    });
                }

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

                var summary = FormatToolSummary(fbName, fbArgs ?? "{}", mcpManager);
                lastActionSummary = summary;
                TrackToolActivity(fbName, fbArgs ?? "{}", filesInspected, filesModified, commandsRun);
                await onEvent(new TrajectoryEventPayload
                {
                    EventType = "tool_call",
                    ToolName = fbName,
                    Content = summary,
                    ToolArgsJson = fbArgs
                });

                var toolResult = await ExecuteBridgedOrLocalToolAsync(
                    fbName,
                    fbArgs ?? "{}",
                    cwd,
                    mcpManager,
                    preset,
                    promptEvent,
                    "fallback-command",
                    isDiscoveryPhase,
                    ct);

                await onEvent(new TrajectoryEventPayload
                {
                    EventType = "tool_result",
                    ToolName = fbName,
                    Content = $"Completed {fbName}",
                    ToolResultJson = toolResult
                });

                // post_job_message with requires_reply=true parks the turn
                if (string.Equals(fbName, "post_job_message", StringComparison.OrdinalIgnoreCase) && IsRequiresReply(fbArgs))
                {
                    var isError = IsToolResultError(toolResult);
                    if (!isError)
                    {
                        askedQuestion = true;
                        responseEmitted = true;
                        await onEvent(new TrajectoryEventPayload
                        {
                            EventType = "response",
                            Content = "Turn paused: plan / question posted to Job Chat awaiting reply."
                        });
                        return;
                    }
                }

                // manage_task with action='set_checklist' yields the turn for Stage 2 Plan Approval
                if (string.Equals(fbName, "manage_task", StringComparison.OrdinalIgnoreCase) && IsSetChecklistAction(fbArgs))
                {
                    var isError = IsToolResultError(toolResult);
                    if (!isError)
                    {
                        var checklistAssistantMessage = new OpenAiMessage { Role = "assistant", Content = finalContent };
                        MaybeAttachReasoningToHistory(checklistAssistantMessage, reasoningBuilder.ToString(), reasoningShape, behaviorProfile, effectiveModel);
                        history.Add(checklistAssistantMessage);
                        history.Add(new OpenAiMessage { Role = "user", Content = $"Tool Result for {fbName}:\n{toolResult}" });
                        SaveHistoryToDisk(promptEvent.SessionId, history);

                        responseEmitted = true;
                        await onEvent(new TrajectoryEventPayload
                        {
                            EventType = "response",
                            Content = "Turn concluded: Living task checklist registered. Awaiting Stage 2 Plan Approval."
                        });
                        return;
                    }
                }

                // open_pull_request yields the turn to code review
                if (string.Equals(fbName, "open_pull_request", StringComparison.OrdinalIgnoreCase))
                {
                    var isError = IsToolResultError(toolResult);
                    if (!isError)
                    {
                        history.Add(new OpenAiMessage { Role = "assistant", Content = finalContent });
                        history.Add(new OpenAiMessage { Role = "user", Content = $"Tool Result for {fbName}:\n{toolResult}" });
                        SaveHistoryToDisk(promptEvent.SessionId, history);

                        // Same terminal-event contract as the native tool-call path above.
                        await onEvent(new TrajectoryEventPayload
                        {
                            EventType = "response",
                            Content = (string.IsNullOrWhiteSpace(finalContent) ? "" : finalContent.Trim() + "\n\n")
                                + "Pull request registered for this branch — turn yielded to the review gate."
                        });
                        responseEmitted = true;
                        return;
                    }
                }

                var fallbackAssistantMessage = new OpenAiMessage { Role = "assistant", Content = finalContent };
                MaybeAttachReasoningToHistory(fallbackAssistantMessage, reasoningBuilder.ToString(), reasoningShape, behaviorProfile, effectiveModel);
                history.Add(fallbackAssistantMessage);
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

            // Continuation nudge for the silent tool-call-drop class: a turn-ending response
            // that PROMISES action without issuing a tool call is not a conclusion — the call
            // was likely swallowed server-side (parsers lose these at chunk boundaries, so a
            // retry frequently succeeds). Re-prompt instead of ending; bounded at 2 per turn so
            // a genuinely stuck model still concludes. Toolless direct mode is exempt.
            if (toolsDefinition != null && intentNudgesUsed < 2 && LooksLikeUnexecutedIntent(effectiveContent))
            {
                intentNudgesUsed++;
                var narratedTurn = new OpenAiMessage { Role = "assistant", Content = effectiveContent };
                MaybeAttachReasoningToHistory(narratedTurn, reasoningBuilder.ToString(), reasoningShape, behaviorProfile, effectiveModel);
                history.Add(narratedTurn);
                history.Add(new OpenAiMessage
                {
                    Role = "user",
                    Content = "[Harness guard] Your previous message announced an action but no tool call arrived and no result is pending (" + intentNudgesUsed + "/2 nudges). If the tool call was lost in transport, issue it again now. If the work is genuinely finished, reply with your final summary and no action promises."
                });
                SaveHistoryToDisk(promptEvent.SessionId, history);
                await onEvent(new TrajectoryEventPayload
                {
                    EventType = "thought",
                    Content = $"[@{preset.Id}] [Harness guard] Turn-ending response narrated an unexecuted action — injected continuation nudge ({intentNudgesUsed}/2)."
                });
                stepCount++;
                continue;
            }

            // Some companion gateways strip streaming `usage` entirely (live forensics: 54 of 62
            // usage rows had inputTokens=0), which leaves cost telemetry blind and never feeds the
            // learned-context-limit path. Fall back to the same chars/3.6 estimator compaction uses,
            // flagged so estimated is never mistaken for measured — and a floor, since the estimate
            // covers only the final step's prompt, not the cumulative tool loop.
            var usageEstimated = false;
            if (totalInputTokens == 0 && totalOutputTokens == 0)
            {
                totalInputTokens = EstimateTotalTokens(history);
                totalOutputTokens = (long)Math.Ceiling(effectiveContent.Length / 3.6);
                totalTokens = totalInputTokens + totalOutputTokens;
                usageEstimated = true;
            }

            var finalAssistantMessage = new OpenAiMessage { Role = "assistant", Content = effectiveContent };
            MaybeAttachReasoningToHistory(finalAssistantMessage, reasoningBuilder.ToString(), reasoningShape, behaviorProfile, effectiveModel);
            history.Add(finalAssistantMessage);

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
                        durationSeconds = duration,
                        estimated = usageEstimated
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
                Content = $"[@{preset.Id}] Reached progress checkpoint ({maxSteps} steps). Generating status summary..."
            });

            string? summaryContent = null;
            try
            {
                var summaryHistory = new List<OpenAiMessage>(history)
                {
                    new OpenAiMessage
                    {
                        Role = "user",
                        Content = $"[System Notice: You have reached the {maxSteps}-step progress checkpoint for this turn. Please summarize what you have accomplished so far, the current status of your changes, and what next steps remain.]"
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

            var detailsSb = new StringBuilder();
            detailsSb.AppendLine($"🔄 **Progress Checkpoint ({maxSteps} steps)**:\n");
            var agentTag = !string.IsNullOrWhiteSpace(promptEvent.PersonaId) ? promptEvent.PersonaId : preset.Id;
            var personaDisplay = !string.IsNullOrWhiteSpace(promptEvent.PersonaName) ? $" ({promptEvent.PersonaName})" : "";
            detailsSb.AppendLine($"Agent `@{agentTag}`{personaDisplay} reached the {maxSteps}-step progress limit for this turn and is rolling over.\n");

            var goal = ExtractGoal(promptEvent);
            if (!string.IsNullOrWhiteSpace(goal))
            {
                detailsSb.AppendLine($"**Active Goal**: {goal}\n");
            }

            if (!string.IsNullOrWhiteSpace(summaryContent))
            {
                detailsSb.AppendLine(summaryContent.Trim());
                detailsSb.AppendLine();
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(lastActionSummary))
                {
                    detailsSb.AppendLine($"**Current Activity**: {lastActionSummary}");
                }
                detailsSb.AppendLine();
            }

            detailsSb.AppendLine("**Turn Progress & Workspace Activity**:");
            detailsSb.AppendLine($"- **Steps**: {stepCount} completed");
            if (filesModified.Count > 0)
            {
                var modFiles = string.Join(", ", filesModified.Take(8).Select(f => $"`{Path.GetFileName(f)}`"));
                var extraMod = filesModified.Count > 8 ? $" (+{filesModified.Count - 8} more)" : "";
                detailsSb.AppendLine($"- **Files Modified** ({filesModified.Count}): {modFiles}{extraMod}");
            }
            if (filesInspected.Count > 0)
            {
                var inspFiles = string.Join(", ", filesInspected.Take(8).Select(f => $"`{Path.GetFileName(f)}`"));
                var extra = filesInspected.Count > 8 ? $" (+{filesInspected.Count - 8} more)" : "";
                detailsSb.AppendLine($"- **Files Inspected** ({filesInspected.Count}): {inspFiles}{extra}");
            }
            if (commandsRun.Count > 0)
            {
                var distinctCmds = commandsRun.Distinct().ToList();
                var cmds = string.Join(", ", distinctCmds.Take(5).Select(c => $"`{(c.Length > 45 ? c[..42] + "..." : c)}`"));
                var extraCmds = distinctCmds.Count > 5 ? $" (+{distinctCmds.Count - 5} more)" : "";
                detailsSb.AppendLine($"- **Commands Run** ({distinctCmds.Count}): {cmds}{extraCmds}");
            }

            detailsSb.Append("\n*Continuing to next turn autonomously. To redirect or interrupt, reply in this chat.*");

            var finalMsg = detailsSb.ToString();

            sw.Stop();
            var duration = sw.Elapsed.TotalSeconds;
            var checkpointUsageEstimated = false;
            if (totalInputTokens == 0 && totalOutputTokens == 0)
            {
                totalInputTokens = EstimateTotalTokens(history);
                totalOutputTokens = (long)Math.Ceiling(finalMsg.Length / 3.6);
                totalTokens = totalInputTokens + totalOutputTokens;
                checkpointUsageEstimated = true;
            }
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
                        durationSeconds = duration,
                        estimated = checkpointUsageEstimated
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

    private static Task<McpClientManager> ConnectMcpServersAsync(
        IReadOnlyList<ResolvedMcpServerConfig> servers,
        AgentPresetConfig preset,
        string cwd,
        Func<TrajectoryEventPayload, Task> onEvent,
        CancellationToken ct)
        => AgentToolExecutor.ConnectMcpServersAsync(servers, preset, cwd, onEvent, ct);

    private static Task<string> ExecuteBridgedOrLocalToolAsync(
        string toolName,
        string argsJson,
        string cwd,
        McpClientManager? mcpManager,
        AgentPresetConfig preset,
        PromptReceivedEvent promptEvent,
        string commandId,
        bool isDiscoveryPhase,
        CancellationToken ct)
        => AgentToolExecutor.ExecuteBridgedOrLocalToolAsync(
            toolName, argsJson, cwd, mcpManager, preset, promptEvent, commandId, isDiscoveryPhase, ct);

    private static string BuildSystemPrompt(AgentPresetConfig preset, PromptReceivedEvent promptEvent, string cwd)
        => AgentToolExecutor.BuildDefaultSystemPrompt(preset, promptEvent, cwd);

    public static List<object> GetToolDefinitions() => AgentToolExecutor.GetToolDefinitions();

    public static List<object> GetToolDefinitions(IEnumerable<object>? extraTools)
        => AgentToolExecutor.GetToolDefinitions(extraTools);

    internal static string ResolveDefaultToolWorkingDirectory(string? cwd)
        => AgentToolExecutor.ResolveDefaultToolWorkingDirectory(cwd);

    public static Task<string> ExecuteToolLocallyAsync(
        string toolName,
        string argsJson,
        string cwd,
        CancellationToken ct,
        int? timeoutSeconds = null,
        Guid? sessionId = null,
        string? commandId = null,
        Func<string, Task>? onOutputChunk = null,
        string? personaId = null,
        string? agentId = null,
        bool isDiscoveryPhase = false)
        => AgentToolExecutor.ExecuteToolLocallyAsync(
            toolName, argsJson, cwd, ct, timeoutSeconds, sessionId, commandId, onOutputChunk, personaId, agentId, isDiscoveryPhase);

    private static string FormatToolSummary(string toolName, string argsJson, McpClientManager? mcpManager = null)
        => AgentToolExecutor.FormatToolSummary(toolName, argsJson, mcpManager);

    private static void TrackToolActivity(
        string toolName,
        string argsJson,
        HashSet<string> filesInspected,
        HashSet<string> filesModified,
        List<string> commandsRun)
        => AgentToolExecutor.TrackToolActivity(toolName, argsJson, filesInspected, filesModified, commandsRun);

    internal static bool IsMutatingGitCommand(string command)
        => AgentToolExecutor.IsMutatingGitCommand(command);

    internal static bool IsReadOnlyTool(string? toolName, string? argsJson, McpClientManager? mcpManager = null)
        => AgentToolExecutor.IsReadOnlyTool(toolName, argsJson, mcpManager);

    internal static bool IsToolResultError(string? result)
        => AgentToolExecutor.IsToolResultError(result);

    internal static bool IsRequiresReply(string? toolArgs)
        => AgentToolExecutor.IsRequiresReply(toolArgs);

    internal static bool IsSetChecklistAction(string? toolArgs)
        => AgentToolExecutor.IsSetChecklistAction(toolArgs);

    internal static (string? ExtractedThought, string CleanedContent) ExtractInlineThinkBlocks(string? content)
        => AgentToolExecutor.ExtractInlineThinkBlocks(content);

    private static string StripRawToolCallTags(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return string.Empty;
        var stripped = Regex.Replace(content, @"<tool_call>\s*[\s\S]*?\s*</tool_call>", string.Empty, RegexOptions.IgnoreCase);
        stripped = Regex.Replace(stripped, @"```(?:json:)?tool_call\s*[\s\S]*?```", string.Empty, RegexOptions.IgnoreCase);
        stripped = Regex.Replace(stripped, @"<function=[\w.\-]+>[\s\S]*?</function>", string.Empty, RegexOptions.IgnoreCase);
        return stripped.Trim();
    }

    internal static (bool HasTool, string? Name, string? Args) TryParseFallbackToolCall(string content)
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

        // Qwen3-Coder/Next XML tool format — the leak shape reasoning/tool parsers have a long
        // documented history of dropping into `content` instead of `tool_calls` (and which this
        // model family demonstrably produced inside this very project session). Served as an
        // execution path so a leaked call still runs instead of ending the turn as prose.
        // When several calls leak in one message, only the first executes per step; the loop
        // re-offers the remaining content to the model, which typically re-emits them.
        var qwenXmlMatch = Regex.Match(content, @"<function=(?<name>[\w.\-]+)>(?<body>.*?)</function>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (qwenXmlMatch.Success)
        {
            var fnName = qwenXmlMatch.Groups["name"].Value.Trim();
            var argsObj = new Dictionary<string, object?>();
            foreach (Match p in Regex.Matches(qwenXmlMatch.Groups["body"].Value,
                         @"<parameter=(?<k>[\w.\-]+)>(?<v>.*?)</parameter>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                argsObj[p.Groups["k"].Value.Trim()] = CoerceXmlParamValue(p.Groups["v"].Value.Trim());
            }
            if (!string.IsNullOrWhiteSpace(fnName))
                return (true, fnName, argsObj.Count == 0 ? "{}" : JsonSerializer.Serialize(argsObj));
        }

        return (false, null, null);
    }

    internal static object? CoerceXmlParamValue(string raw)
    {
        if (raw.Length == 0) return "";
        if (long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l))
            return l; // integers stay integers so downstream TryGetInt32 keeps working
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
            return d;
        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase)) return null;
        return raw;
    }

    internal static string? GetPropertyString(JsonElement root, params string[] names)
        => AgentToolExecutor.GetPropertyString(root, names);

    internal static string ExtractGoal(PromptReceivedEvent promptEvent)
        => AgentToolExecutor.ExtractGoal(promptEvent);

    internal static bool GetPropertyBool(JsonElement root, params string[] names)
        => AgentToolExecutor.GetPropertyBool(root, names);

    internal static int CountSubstringOccurrences(string text, string value)
        => AgentToolExecutor.CountSubstringOccurrences(text, value);

    internal static List<AgentToolExecutor.NormalizedMatch> FindOccurrencesNormalized(string text, string targetLinesJoined)
        => AgentToolExecutor.FindOccurrencesNormalized(text, targetLinesJoined);

    internal static string StripDisplayLinePrefixesFromEachLine(string target)
        => AgentToolExecutor.StripDisplayLinePrefixesFromEachLine(target);

    internal static bool IsBenignExitOneCommand(string command)
        => AgentToolExecutor.IsBenignExitOneCommand(command);

    internal static string ReplaceFirstOrdinal(string text, string search, string replace)
        => AgentToolExecutor.ReplaceFirstOrdinal(text, search, replace);

    internal static async Task<bool> IsTcpPortOpenAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(1000);
            await client.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Launches a companion daemon (per its configured command line) detached, with its stdio
    /// discarded so a long-lived proxy cannot keep the runner's own output pipes open.
    /// </summary>
    internal static void StartCompanionDetached(string command)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        try
        {
            // Detach at the shell level: the daemon's fds are repointed at /dev/null before exec,
            // so it cannot hold the runner's own stdout/stderr pipes open past our exit, and the
            // outer sh returns immediately instead of leaving us a zombie to reap.
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"( {command} ) </dev/null >/dev/null 2>&1 &");
            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
        }
        catch
        {
            // Readiness is re-probed on the port; a failed launch surfaces as not-ready.
        }
    }

    /// <summary>
    /// Runs a shell check (e.g. the driver's auth verification command) and reports whether it
    /// passed. Returns null when the check could not run or timed out — unknown, not failed.
    /// </summary>
    internal static async Task<bool?> RunShellCheckAsync(string command, int timeoutSeconds, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return null;
        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add(command);
            proc = Process.Start(psi);
            if (proc == null) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            await proc.WaitForExitAsync(timeout.Token);
            return proc.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // timed out — unknown, not failed; the finally reaps it
        }
        catch
        {
            return null;
        }
        finally
        {
            if (proc != null)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                proc.Dispose();
            }
        }
    }

    /// <summary>
    /// Pre-flight for the driver-companion port fronting a loopback OpenAI-compatible endpoint.
    /// </summary>
    /// <remarks>
    /// Every fact here comes from the generic <see cref="DriverSidecarConfig"/> fields — command
    /// line, port, login and auth-check commands — so no driver binary is named and any companion
    /// that "listens on a local port, is started by a command, and is verified by a command" gets
    /// the same treatment. Live-trajectory forensics (2026-11-15) showed persona turns burning
    /// their retry budget against a companion that was simply not listening; a 20s pre-flight with
    /// an actionable guidance string is cheaper than three backoffs against a dead socket, and
    /// catches expired companion logins before they masquerade as model failures.
    /// </remarks>
    internal static async Task<(bool Ready, string? Guidance)> EnsureCompanionReadyAsync(
        AgentPresetConfig preset,
        string endpoint,
        Func<TrajectoryEventPayload, Task> onEvent,
        CancellationToken ct)
    {
        var sc = preset.Sidecar;
        if (sc is not { Enabled: true } || sc.CompanionPort is not int companionPort)
            return (true, null);

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
            !(endpointUri.IsLoopback || endpointUri.Host is "localhost" or "127.0.0.1" or "::1"))
        {
            return (true, null); // companion fronts a remote route we cannot health-probe from here
        }

        if (await IsTcpPortOpenAsync("127.0.0.1", companionPort, ct))
            return (true, null);

        if (string.IsNullOrWhiteSpace(sc.CompanionCommand))
        {
            return (false,
                $"No driver companion is listening on 127.0.0.1:{companionPort} and the driver profile sets no companion command to start it. " +
                $"Start the daemon that serves {endpoint} before dispatching again" +
                (!string.IsNullOrWhiteSpace(sc.LoginCommand) ? $", and verify sign-in with: {sc.LoginCommand}" : "") + ".");
        }

        await onEvent(new TrajectoryEventPayload
        {
            EventType = "thought",
            Content = $"[@{preset.Id}] Driver companion is not listening on 127.0.0.1:{companionPort}. Starting it: `{sc.CompanionCommand}`..."
        });

        StartCompanionDetached(sc.CompanionCommand);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        var up = false;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsTcpPortOpenAsync("127.0.0.1", companionPort, ct))
            {
                up = true;
                break;
            }
            await Task.Delay(500, ct);
        }

        if (!up)
        {
            return (false,
                $"Started the driver companion (`{sc.CompanionCommand}`) but nothing came up on 127.0.0.1:{companionPort} within 20s. " +
                "Check the companion daemon's logs on the host serving " + endpoint +
                (!string.IsNullOrWhiteSpace(sc.AuthCheckCommand) ? $"; verify with: `{sc.AuthCheckCommand}`" : "") +
                (!string.IsNullOrWhiteSpace(sc.LoginCommand) ? $", and sign in with: `{sc.LoginCommand}`" : "") + ".");
        }

        await onEvent(new TrajectoryEventPayload
        {
            EventType = "thought",
            Content = $"[@{preset.Id}] Driver companion ready on 127.0.0.1:{companionPort}."
        });

        // Only pay the cost of an auth probe when we just (re)started the daemon — a warm companion
        // that is mid-expiry will still fail fast with a 401, which gets its own hint below.
        if (!string.IsNullOrWhiteSpace(sc.AuthCheckCommand))
        {
            var authOk = await RunShellCheckAsync(sc.AuthCheckCommand, 15, ct);
            if (authOk == false)
            {
                await onEvent(new TrajectoryEventPayload
                {
                    EventType = "thought",
                    Content = $"[@{preset.Id}] [Companion guard] Auth check `{sc.AuthCheckCommand}` FAILED after a cold companion start. " +
                              (!string.IsNullOrWhiteSpace(sc.LoginCommand)
                                  ? $"Sign in with `{sc.LoginCommand}` on the host serving {endpoint} — turns will be rejected until then."
                                  : "The companion appears to need sign-in on the host serving the endpoint.")
                });
            }
            else if (authOk == true)
            {
                await onEvent(new TrajectoryEventPayload
                {
                    EventType = "thought",
                    Content = $"[@{preset.Id}] Companion auth check passed."
                });
            }
        }

        return (true, null);
    }

    /// <summary>
    /// Config-derived guidance for companion auth/connection failures, so hint text describes the
    /// driver's actual commands instead of naming any particular daemon binary.
    /// </summary>
    internal static string? BuildCompanionAuthHint(AgentPresetConfig preset)
    {
        var sc = preset.Sidecar;
        if (sc is not { Enabled: true }) return null;
        var login = !string.IsNullOrWhiteSpace(sc.LoginCommand) ? sc.LoginCommand : preset.LoginCommand;
        var authCheck = !string.IsNullOrWhiteSpace(sc.AuthCheckCommand) ? sc.AuthCheckCommand : preset.AuthCheckCommand;
        if (string.IsNullOrWhiteSpace(login) && string.IsNullOrWhiteSpace(authCheck)) return null;
        var parts = new List<string> { "The driver companion fronting this endpoint appears not to be signed in." };
        if (!string.IsNullOrWhiteSpace(authCheck)) parts.Add($"Verify with `{authCheck}`");
        if (!string.IsNullOrWhiteSpace(login)) parts.Add($"sign in with `{login}`");
        return string.Join("; ", parts) + " on the host serving the endpoint, then retry.";
    }

    // ---- model-behavior profile wiring (see Core ModelBehaviorProfiles) ----

    private static SamplingDialect? ParseDialectOverride(string? raw) =>
        Enum.TryParse<SamplingDialect>(raw, ignoreCase: true, out var d) ? d : null;

    private static ThinkingRoundTrip? ParseThinkingOverride(string? raw) =>
        Enum.TryParse<ThinkingRoundTrip>(raw, ignoreCase: true, out var t) ? t : null;

    internal static List<string> ApplyProfileSampling(Dictionary<string, object?> reqBody, ModelBehaviorProfile profile, string model)
    {
        var sent = new List<string>();
        if (profile.Sampling == null) return sent;
        foreach (var kvp in profile.Sampling)
        {
            if (!profile.MaySendSamplingKey(model, kvp.Key)) continue;
            reqBody[kvp.Key] = kvp.Value == Math.Floor(kvp.Value) && Math.Abs(kvp.Value) < 1e15
                ? (long)kvp.Value
                : kvp.Value;
            sent.Add(kvp.Key);
        }
        return sent;
    }

    /// <summary>
    /// Matches endpoint parameter names against a 400 body, restricted to the keys WE added, so
    /// learned pruning can never delete a base request field. Requires a parameter-rejection
    /// phrase in the body to avoid mis-learning on unrelated 400s.
    /// </summary>
    internal static List<string> ParseRejectedRequestParameters(string errorContent, IReadOnlyCollection<string> candidateKeys)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(errorContent) || candidateKeys.Count == 0) return result;
        var looksLikeParamRejection =
            errorContent.Contains("unrecognized", StringComparison.OrdinalIgnoreCase) ||
            errorContent.Contains("unknown parameter", StringComparison.OrdinalIgnoreCase) ||
            errorContent.Contains("extra inputs", StringComparison.OrdinalIgnoreCase) ||
            errorContent.Contains("not permitted", StringComparison.OrdinalIgnoreCase) ||
            errorContent.Contains("additional properties", StringComparison.OrdinalIgnoreCase) ||
            errorContent.Contains("unexpected parameter", StringComparison.OrdinalIgnoreCase) ||
            errorContent.Contains("unsupported parameter", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeParamRejection) return result;
        foreach (var key in candidateKeys)
        {
            if (Regex.IsMatch(errorContent, $@"\b{Regex.Escape(key)}\b", RegexOptions.IgnoreCase))
                result.Add(key);
        }
        return result;
    }

    internal sealed class ReasoningShapeTracker
    {
        public bool SawField { get; private set; }
        public bool SawInlineTags { get; private set; }

        public void NoteField() => SawField = true;

        public void NoteInline(string? textChunk)
        {
            if (!string.IsNullOrEmpty(textChunk) &&
                (textChunk.Contains("<think>") || textChunk.Contains("</think>")))
            {
                SawInlineTags = true;
            }
        }
    }

    /// <summary>
    /// Round-trips the model's reasoning into assistant history per the resolved policy.
    /// Qwen's official guidance is that stripping reasoning from multi-step tool history
    /// measurably degrades quality, so Echo mode mirrors the inbound shape: reasoning that
    /// arrived as a dedicated field goes back as that field; reasoning that stayed inline in
    /// content needs no action (it is already there). Inline re-embedding is tag-balanced or
    /// skipped — an unclosed think block corrupts every later turn's rendering.
    /// </summary>
    internal static void MaybeAttachReasoningToHistory(
        OpenAiMessage assistantMessage,
        string reasoningText,
        ReasoningShapeTracker shape,
        ModelBehaviorProfile profile,
        string model)
    {
        if (string.IsNullOrWhiteSpace(reasoningText)) return;
        var mode = profile.ThinkingRoundTripMode;
        if (mode == ThinkingRoundTrip.Off) return;

        var rejected = ModelBehaviorProfiles.IsReasoningFieldRejected(model);
        var useField = mode switch
        {
            ThinkingRoundTrip.Field => !rejected,
            ThinkingRoundTrip.Inline => false,
            _ => shape.SawField && !rejected,
        };

        if (useField)
        {
            assistantMessage.ReasoningContent = reasoningText.Trim();
            return;
        }

        // Inline: forced mode, or Echo downgraded after the endpoint rejected the field —
        // which is the vendor's documented workaround verbatim ("pass the content as it is").
        var safe = ModelBehaviorProfiles.SanitizeInlineThinking(reasoningText);
        if (safe == null) return;
        var content = assistantMessage.Content;
        if (content != null && (content.Contains("<think>") || content.Contains("</think>"))) return; // already inline
        assistantMessage.Content = $"<think>\n{safe}\n</think>\n\n" + (content ?? "");
    }

    private static void ParseOpenAiChunkOrMessage(
        JsonElement root,
        StringBuilder contentBuilder,
        StringBuilder reasoningBuilder,
        SortedDictionary<int, (string? Id, StringBuilder Name, StringBuilder Args)> toolCallsByIndex,
        ref long totalInputTokens,
        ref long totalOutputTokens,
        ref long totalTokens,
        ReasoningShapeTracker? shape = null)
    {
        // Check usage in chunk/message
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
            return;

        var choice = choicesElem[0];
        var elem = choice.TryGetProperty("delta", out var deltaElem) ? deltaElem : (choice.TryGetProperty("message", out var msgElem) ? msgElem : choice);

        // Content
        if (elem.TryGetProperty("content", out var cProp) && cProp.ValueKind == JsonValueKind.String)
        {
            var textChunk = cProp.GetString();
            if (!string.IsNullOrEmpty(textChunk))
            {
                contentBuilder.Append(textChunk);
                shape?.NoteInline(textChunk);
            }
        }
        else if (choice.TryGetProperty("text", out var tProp) && tProp.ValueKind == JsonValueKind.String)
        {
            var textChunk = tProp.GetString();
            if (!string.IsNullOrEmpty(textChunk))
            {
                contentBuilder.Append(textChunk);
                shape?.NoteInline(textChunk);
            }
        }

        // Delta reasoning / thought (DeepSeek-R1, Qwen reasoning, etc.)
        if ((elem.TryGetProperty("reasoning_content", out var rcProp) ||
             elem.TryGetProperty("reasoning", out rcProp) ||
             elem.TryGetProperty("thought", out rcProp) ||
             elem.TryGetProperty("reasoning_text", out rcProp)) &&
            rcProp.ValueKind == JsonValueKind.String)
        {
            var reasoningChunk = rcProp.GetString();
            if (!string.IsNullOrEmpty(reasoningChunk))
            {
                reasoningBuilder.Append(reasoningChunk);
                shape?.NoteField();
            }
        }

        // Tool calls
        if (elem.TryGetProperty("tool_calls", out var tcProp) && tcProp.ValueKind == JsonValueKind.Array)
        {
            int fallbackIdx = 0;
            foreach (var tcElem in tcProp.EnumerateArray())
            {
                var idx = tcElem.TryGetProperty("index", out var iProp) ? iProp.GetInt32() : fallbackIdx++;
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

    /// <summary>
    /// The model's own reasoning, round-tripped in assistant history per the resolved
    /// <see cref="ThinkingRoundTrip"/> policy. Emitted only when set — servers that never
    /// separated reasoning will never see this field.
    /// </summary>
    [JsonPropertyName("reasoning_content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningContent { get; set; }

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
        ReasoningContent = ReasoningContent,
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
        if (ReasoningContent != null) dict["reasoning_content"] = ReasoningContent;
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
