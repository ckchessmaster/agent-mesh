using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentMesh.Cli.Models;
using AgentMesh.Cli.Services;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;

namespace AgentMesh.Cli.Adapters;

/// <summary>
/// Standalone adapter targeting OpenRouter's Chat Completions API (<c>/api/v1/chat/completions</c>).
/// Implements OpenRouter prompt caching (<c>cache_control</c> + sticky <c>session_id</c>),
/// verbatim <c>reasoning.effort</c> forwarding, multi-turn <c>reasoning_details</c> preservation,
/// provider routing, fallback model lists, and exact cost/cache telemetry.
/// </summary>
public class OpenRouterAdapter : IAgentAdapter, IDescribesOwnCapabilities
{
    public string AdapterType => DriverAdapters.OpenRouter;

    private const int DefaultStreamChunkIdleTimeoutSeconds = 120;
    private const int DefaultTimeoutSeconds = 600;
    private const int DefaultMaxRetries = 2;
    private const int DefaultMaxSteps = 50;
    private const int DefaultCompactionThresholdPercent = 75;
    internal const string TaskEnvelopeMarker = "[TASK ENVELOPE & WORKING ENVIRONMENT]";
    internal const string SummaryMarker = "[Conversation Summary";
    private const int SummaryTranscriptMaxChars = 120_000;

    private static readonly ConcurrentDictionary<Guid, List<OpenRouterMessage>> SessionHistories = new();
    private static readonly HttpClient DefaultHttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly object HistoryDiskLock = new();

    public static HttpClient HttpClientInstance = DefaultHttpClient;

    public static void SetHttpClientForTest(HttpClient client) => HttpClientInstance = client;

    public static void ResetHttpClientForTest() => HttpClientInstance = DefaultHttpClient;

    /// <summary>
    /// Optional callback invoked when a context-window overflow error reveals a model's true token limit.
    /// Args: (driverId, modelId, maxContextTokens).
    /// </summary>
    public static Func<string, string, int, Task>? LearnedContextLimitReporter { get; set; }

    public static string HistoryDirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".agentmesh",
        "openrouter_histories");

    public static void SaveHistoryToDisk(Guid sessionId, List<OpenRouterMessage> history)
    {
        try
        {
            lock (HistoryDiskLock)
            {
                if (!Directory.Exists(HistoryDirectoryPath)) Directory.CreateDirectory(HistoryDirectoryPath);
                var filePath = Path.Combine(HistoryDirectoryPath, $"{sessionId}.json");
                File.WriteAllText(filePath, JsonSerializer.Serialize(history));
            }
        }
        catch { }
    }

    public static List<OpenRouterMessage>? LoadHistoryFromDisk(Guid sessionId)
    {
        try
        {
            lock (HistoryDiskLock)
            {
                var filePath = Path.Combine(HistoryDirectoryPath, $"{sessionId}.json");
                if (File.Exists(filePath))
                {
                    return JsonSerializer.Deserialize<List<OpenRouterMessage>>(File.ReadAllText(filePath));
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

    public static List<OpenRouterMessage>? GetSessionHistory(Guid sessionId)
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

    internal static string? ResolveApiKey(AgentPresetConfig preset)
    {
        if (!string.IsNullOrWhiteSpace(preset.ApiKey))
            return preset.ApiKey.Trim();

        var envKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        return string.IsNullOrWhiteSpace(envKey) ? null : envKey.Trim();
    }

    internal static string ResolveEndpoint(AgentPresetConfig preset)
    {
        if (!string.IsNullOrWhiteSpace(preset.Endpoint))
            return preset.Endpoint.Trim();

        var envUrl = Environment.GetEnvironmentVariable("OPENROUTER_BASE_URL");
        return string.IsNullOrWhiteSpace(envUrl) ? OpenRouterConfig.DefaultEndpoint : envUrl.Trim();
    }

    internal static string ResolveChatUrl(string endpoint)
    {
        var trimmed = endpoint.Trim().TrimEnd('/');
        return trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed}/chat/completions";
    }

    internal static string ResolveStickySessionId(PromptReceivedEvent promptEvent) =>
        !string.IsNullOrWhiteSpace(promptEvent.JobId)
            ? $"job:{promptEvent.JobId.Trim()}"
            : $"session:{promptEvent.SessionId}";

    public static int ResolveTimeoutSeconds(AgentPresetConfig preset)
    {
        if (preset.TimeoutSeconds is > 0) return preset.TimeoutSeconds.Value;
        if (int.TryParse(Environment.GetEnvironmentVariable("OPENROUTER_TIMEOUT_SECONDS"), out var envSec) && envSec > 0)
            return envSec;
        return DefaultTimeoutSeconds;
    }

    public static int ResolveMaxRetries(AgentPresetConfig preset)
    {
        if (preset.MaxRetries is >= 0) return preset.MaxRetries.Value;
        if (int.TryParse(Environment.GetEnvironmentVariable("OPENROUTER_MAX_RETRIES"), out var envRetries) && envRetries >= 0)
            return envRetries;
        return DefaultMaxRetries;
    }

    public static int ResolveMaxSteps(AgentPresetConfig preset, int? globalDefault = null)
    {
        if (preset.MaxSteps is > 0) return preset.MaxSteps.Value;
        if (globalDefault is > 0) return globalDefault.Value;
        if (int.TryParse(Environment.GetEnvironmentVariable("AGENTMESH_MAX_STEPS"), out var meshSteps) && meshSteps > 0)
            return meshSteps;
        if (int.TryParse(Environment.GetEnvironmentVariable("AGENT_MAX_STEPS"), out var agentSteps) && agentSteps > 0)
            return agentSteps;
        return DefaultMaxSteps;
    }

    public static int ResolveModelContextLimit(string model, string? contextSizeOption, AgentPresetConfig? preset = null)
    {
        if (ModelContextLimits.ParseOption(contextSizeOption) is int explicitTokens && explicitTokens > 0)
            return explicitTokens;
        if (preset?.ContextWindowTokens is int presetTokens && presetTokens > 0)
            return presetTokens;
        if (ModelContextLimits.ParseOption(preset?.DefaultContextSize) is int presetOptionTokens && presetOptionTokens > 0)
            return presetOptionTokens;
        return ModelContextLimits.ResolveTokens(model, contextSizeOption);
    }

    public static int ResolveCompactionThresholdPercent(AgentPresetConfig? preset = null)
    {
        if (preset?.CompactionThresholdPercent is int configured && configured > 0)
            return Math.Clamp(configured, 10, 95);
        return DefaultCompactionThresholdPercent;
    }

    public static int ResolveStreamChunkIdleTimeoutSeconds(AgentPresetConfig? preset = null)
    {
        if (preset?.StreamChunkIdleTimeoutSeconds is int configured && configured > 0)
            return configured;
        return DefaultStreamChunkIdleTimeoutSeconds;
    }

    public static int GetRetryDelayMilliseconds(HttpResponseMessage? response, int attempt, AgentPresetConfig? preset = null)
    {
        if (preset?.RetryDelayMilliseconds is >= 0)
            return preset.RetryDelayMilliseconds.Value;

        if (response != null && response.Headers.TryGetValues("Retry-After", out var values))
        {
            var first = values.FirstOrDefault();
            if (int.TryParse(first, out var sec) && sec > 0 && sec <= 60)
                return sec * 1000;
        }

        return Math.Min(15_000, (int)Math.Pow(2, attempt - 1) * 1000);
    }

    public static bool IsRetryableStatusCode(int statusCode) =>
        statusCode is 408 or 429 or 500 or 502 or 503 or 504;

    /// <inheritdoc />
    public Task<DriverCapabilityDeclaration> DescribeCapabilitiesAsync(
        AgentPresetConfig preset,
        CancellationToken ct = default)
        => DriverCapabilityDescriber.DescribeOpenRouterAsync(preset, ResolveApiKey(preset), ct);

    public static int EstimateMessageTokens(OpenRouterMessage message)
    {
        var charCount = (message.Role?.Length ?? 0) + (message.Content?.Length ?? 0);
        if (message.ReasoningDetails != null)
        {
            foreach (var item in message.ReasoningDetails)
            {
                charCount += item.GetRawText().Length;
            }
        }
        if (message.ContentParts != null)
        {
            foreach (var part in message.ContentParts)
            {
                if (part is OpenRouterTextContentPart textPart && !string.IsNullOrEmpty(textPart.Text))
                    charCount += textPart.Text.Length;
                else
                    charCount += 3600;
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

    public static int EstimateTotalTokens(IEnumerable<OpenRouterMessage> messages) =>
        messages.Sum(EstimateMessageTokens);

    public static (bool IsContextLimit, int? PromptTokens, int? ContextLimit) ParseContextWindowError(string errorContent)
    {
        if (string.IsNullOrWhiteSpace(errorContent)) return (false, null, null);

        var m1 = Regex.Match(errorContent, @"prompt\s*\(([0-9]+)\s*tokens\).*?context\s*\(([0-9]+)\)", RegexOptions.IgnoreCase);
        if (m1.Success && int.TryParse(m1.Groups[1].Value, out var pt) && int.TryParse(m1.Groups[2].Value, out var cl))
            return (true, pt, cl);

        var m2 = Regex.Match(errorContent, @"maximum context length is\s*([0-9]+)\s*tokens.*?resulted in\s*([0-9]+)\s*tokens", RegexOptions.IgnoreCase);
        if (m2.Success && int.TryParse(m2.Groups[1].Value, out var cl2) && int.TryParse(m2.Groups[2].Value, out var pt2))
            return (true, pt2, cl2);

        var m3 = Regex.Match(errorContent, @"context\s*length\s*exceeded.*?([0-9]+)", RegexOptions.IgnoreCase);
        if (m3.Success && int.TryParse(m3.Groups[1].Value, out var cl3))
            return (true, null, cl3);

        if (errorContent.Contains("exceeds the context window", StringComparison.OrdinalIgnoreCase) ||
            errorContent.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase) ||
            errorContent.Contains("maximum context length", StringComparison.OrdinalIgnoreCase) ||
            errorContent.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase) ||
            (errorContent.Contains("context", StringComparison.OrdinalIgnoreCase) && errorContent.Contains("too large", StringComparison.OrdinalIgnoreCase)))
        {
            return (true, null, null);
        }

        return (false, null, null);
    }

    internal static bool HasUsableStack(PromptStackWirePayload? stack) =>
        stack is not null &&
        !string.IsNullOrWhiteSpace(stack.CachedSystemPrompt) &&
        !string.IsNullOrWhiteSpace(stack.UserTaskEnvelope);

    internal static bool IsTaskEnvelope(OpenRouterMessage? message) =>
        message is { Role: "user" } &&
        message.Content != null &&
        message.Content.StartsWith(TaskEnvelopeMarker, StringComparison.Ordinal);

    internal static bool IsPinnedSummary(OpenRouterMessage? message) =>
        message is { Role: "system" } &&
        message.Content != null &&
        message.Content.StartsWith(SummaryMarker, StringComparison.Ordinal);

    internal static int FirstPrunableIndex(List<OpenRouterMessage> history)
    {
        var index = 1;
        while (index < history.Count && (IsTaskEnvelope(history[index]) || IsPinnedSummary(history[index])))
        {
            index++;
        }
        return index;
    }

    internal static int SanitizeMessageOrder(List<OpenRouterMessage> history)
    {
        var relocated = 0;
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

    public static bool CompactHistory(List<OpenRouterMessage> history, int targetMaxTokens, bool forceAggressive = false)
    {
        if (history.Count <= 1) return false;

        var modified = false;
        var protectedTailCount = forceAggressive ? 2 : 4;
        var floor = FirstPrunableIndex(history);
        var cutoff = Math.Max(floor, history.Count - protectedTailCount);

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
            history.Insert(first, new OpenRouterMessage
            {
                Role = "system",
                Content = $"[Context Compaction: {prunedTurns} earlier conversation turns were pruned to keep within the model context window. Work continues from recent context below.]"
            });
        }

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
                        msg.Content = AgentToolExecutor.TruncateToolResult(msg.Content, maxChars);
                        modified = true;
                    }
                }
            }
            return true;
        }

        return modified;
    }

    public static async Task<bool> TrySummarizeOldTurnsAsync(
        List<OpenRouterMessage> history,
        int protectedTailCount,
        string chatUrl,
        string model,
        string? apiKey,
        AgentPresetConfig preset,
        string stickySessionId,
        int timeoutSeconds,
        CancellationToken ct)
    {
        if (history.Count <= 2) return false;

        var floor = FirstPrunableIndex(history);
        if (history.Count - floor <= protectedTailCount) return false;

        var cutoff = Math.Max(floor, history.Count - protectedTailCount);
        while (cutoff > floor && cutoff < history.Count && history[cutoff].Role == "tool") cutoff--;
        if (cutoff <= floor) return false;

        var middle = history.GetRange(floor, cutoff - floor);

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
            ["max_tokens"] = 3000,
            ["session_id"] = stickySessionId
        };

        try
        {
            using var httpReq = CreateRequest(HttpMethod.Post, chatUrl, apiKey, preset, stickySessionId, acceptSse: false);
            httpReq.Content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(120, timeoutSeconds)));
            using var res = await HttpClientInstance.SendAsync(httpReq, cts.Token);
            if (!res.IsSuccessStatusCode) return false;

            var body = await res.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(body);
            var summary = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(summary)) return false;

            history.RemoveRange(floor, cutoff - floor);
            history.Insert(floor, new OpenRouterMessage
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

    public async Task ExecutePromptAsync(
        AgentPresetConfig preset,
        PromptReceivedEvent promptEvent,
        Func<TrajectoryEventPayload, Task> onEvent,
        CancellationToken ct)
    {
        var apiKey = ResolveApiKey(preset);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            await onEvent(new TrajectoryEventPayload
            {
                EventType = "response",
                Content = "❌ OpenRouter API key is not configured. Configure `apiKey` on this driver or set `OPENROUTER_API_KEY`."
            });
            return;
        }

        var cwd = AgentAdapterFactory.ResolveWorkspaceDirectory(promptEvent);
        var endpoint = ResolveEndpoint(preset);
        var chatUrl = ResolveChatUrl(endpoint);
        var stickySessionId = ResolveStickySessionId(promptEvent);

        var effectiveModel = !string.IsNullOrWhiteSpace(promptEvent.Model) &&
                             !promptEvent.Model.Equals("default", StringComparison.OrdinalIgnoreCase) &&
                             !promptEvent.Model.Equals("custom", StringComparison.OrdinalIgnoreCase)
            ? promptEvent.Model.Trim()
            : !string.IsNullOrWhiteSpace(preset.DefaultModel)
                ? preset.DefaultModel.Trim()
                : "openrouter/auto";

        var timeoutSeconds = ResolveTimeoutSeconds(preset);
        var maxRetries = ResolveMaxRetries(preset);
        var modelContextLimit = ResolveModelContextLimit(effectiveModel, promptEvent.ContextSize, preset);
        var compactionThresholdPercent = ResolveCompactionThresholdPercent(preset);
        var streamChunkIdleTimeoutSeconds = ResolveStreamChunkIdleTimeoutSeconds(preset);

        ct.ThrowIfCancellationRequested();
        await onEvent(new TrajectoryEventPayload
        {
            EventType = "thought",
            Content = $"[@{preset.Id}] Connecting to OpenRouter at {endpoint} using model '{effectiveModel}' (timeout: {timeoutSeconds}s, context: {modelContextLimit} tokens)..."
        });

        var history = InitializeOrLoadHistory(preset, promptEvent, cwd, effectiveModel);
        var (isReadOnlyPhase, isDirectMode) = ResolveExecutionPhase(promptEvent);

        await using var mcpManager = (!isDirectMode && promptEvent.McpServers is { Count: > 0 })
            ? await AgentToolExecutor.ConnectMcpServersAsync(promptEvent.McpServers, preset, cwd, onEvent, ct)
            : null;

        var toolsDefinition = isDirectMode
            ? null
            : AgentToolExecutor.GetToolDefinitions(mcpManager?.GetOpenAiToolDefinitions());

        async Task<(bool Changed, bool Summarized)> CompactAsync(int target, bool aggressive)
        {
            var summarized = await TrySummarizeOldTurnsAsync(
                history, aggressive ? 2 : 4, chatUrl, effectiveModel, apiKey, preset, stickySessionId, timeoutSeconds, ct);
            var changed = CompactHistory(history, target, aggressive);
            if (changed || summarized) SaveHistoryToDisk(promptEvent.SessionId, history);
            return (changed || summarized, summarized);
        }

        var maxSteps = ResolveMaxSteps(preset);
        var stepCount = 0;
        var readOnlyStreak = 0;
        var warnedBudget = false;
        var intentNudgesUsed = 0;
        var streamRetryAttempt = 0;
        var cacheLeadingSystem = true;
        var reasoningRejected = false;
        var responseEmitted = false;
        var askedQuestion = false;
        var filesInspected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filesModified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var commandsRun = new List<string>();
        string? lastActionSummary = null;

        var turnUsage = new TurnUsageAccumulator();
        var sw = Stopwatch.StartNew();

        while (stepCount < maxSteps)
        {
            ct.ThrowIfCancellationRequested();

            var targetMaxTokens = (int)Math.Floor(modelContextLimit * (compactionThresholdPercent / 100.0));
            if (EstimateTotalTokens(history) > targetMaxTokens)
            {
                var beforeEst = EstimateTotalTokens(history);
                var (compacted, summarized) = await CompactAsync(targetMaxTokens, false);
                if (compacted)
                {
                    await onEvent(new TrajectoryEventPayload
                    {
                        EventType = "thought",
                        Content = $"[@{preset.Id}] Proactively {(summarized ? "summarized" : "truncated")} conversation history ({beforeEst} -> {EstimateTotalTokens(history)} tokens, threshold {compactionThresholdPercent}%) to remain within {modelContextLimit} context limit."
                    });
                }
            }

            HttpResponseMessage? httpRes = null;
            CancellationTokenSource? outerRequestCts = null;
            var retryAttempt = 0;
            var contentBuilder = new StringBuilder();
            var reasoningBuilder = new StringBuilder();
            var reasoningDetailsAcc = new OpenRouterReasoningDetailsAccumulator();
            var toolCallsByIndex = new SortedDictionary<int, (string? Id, StringBuilder Name, StringBuilder Args)>();
            var stepUsage = new StepUsageCapture();
            Exception? streamDropped = null;
            string? streamErrorMessage = null;

            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    outerRequestCts?.Dispose();
                    outerRequestCts = null;

                    var dispatchMessages = history.Select(m => m.Clone()).ToList();
                    var relocated = SanitizeMessageOrder(dispatchMessages);
                    if (relocated > 0)
                    {
                        await onEvent(new TrajectoryEventPayload
                        {
                            EventType = "thought",
                            Content = $"[@{preset.Id}] [Harness guard] Relocated {relocated} trailing system message(s) into the leading system block before dispatch."
                        });
                    }

                    var reqBody = BuildRequestBody(
                        effectiveModel,
                        dispatchMessages,
                        preset,
                        promptEvent,
                        stickySessionId,
                        toolsDefinition,
                        cacheLeadingSystem,
                        reasoningRejected);

                    using var httpReq = CreateRequest(HttpMethod.Post, chatUrl, apiKey, preset, stickySessionId, acceptSse: true);
                    httpReq.Content = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json");

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
                                Content = $"[@{preset.Id}] OpenRouter request timed out after {timeoutSeconds}s. Retrying in {delayMs / 1000.0:F1}s (attempt {retryAttempt}/{maxRetries})..."
                            });
                            await Task.Delay(delayMs, ct);
                            continue;
                        }

                        await onEvent(new TrajectoryEventPayload
                        {
                            EventType = "response",
                            Content = $"❌ OpenRouter request ({chatUrl}) timed out after {timeoutSeconds}s."
                        });
                        return;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException)
                    {
                        if (retryAttempt < maxRetries)
                        {
                            retryAttempt++;
                            var delayMs = GetRetryDelayMilliseconds(null, retryAttempt, preset);
                            await onEvent(new TrajectoryEventPayload
                            {
                                EventType = "thought",
                                Content = $"[@{preset.Id}] Network error connecting to OpenRouter ({ex.Message}). Retrying in {delayMs / 1000.0:F1}s (attempt {retryAttempt}/{maxRetries})..."
                            });
                            await Task.Delay(delayMs, ct);
                            continue;
                        }

                        await onEvent(new TrajectoryEventPayload
                        {
                            EventType = "response",
                            Content = $"❌ Failed to connect to OpenRouter ({chatUrl}): {ex.Message}"
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
                            if (parsedLimit is > 0)
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

                        if (statusCode == 400 && cacheLeadingSystem && LooksLikeMultipartSystemRejection(errContent))
                        {
                            cacheLeadingSystem = false;
                            httpRes.Dispose();
                            await onEvent(new TrajectoryEventPayload
                            {
                                EventType = "thought",
                                Content = $"[@{preset.Id}] Model rejected multipart cache_control system block; retrying with plain text system message."
                            });
                            continue;
                        }

                        if (statusCode == 400 && !reasoningRejected &&
                            !string.IsNullOrWhiteSpace(promptEvent.Effort) &&
                            errContent.Contains("reasoning", StringComparison.OrdinalIgnoreCase))
                        {
                            reasoningRejected = true;
                            httpRes.Dispose();
                            await onEvent(new TrajectoryEventPayload
                            {
                                EventType = "thought",
                                Content = $"[@{preset.Id}] Model '{effectiveModel}' rejected reasoning parameter; retrying without reasoning.effort."
                            });
                            continue;
                        }

                        if (IsRetryableStatusCode(statusCode) && retryAttempt < maxRetries)
                        {
                            retryAttempt++;
                            var delayMs = GetRetryDelayMilliseconds(httpRes, retryAttempt, preset);
                            await onEvent(new TrajectoryEventPayload
                            {
                                EventType = "thought",
                                Content = $"[@{preset.Id}] OpenRouter returned HTTP {statusCode} ({httpRes.ReasonPhrase}). Retrying in {delayMs / 1000.0:F1}s (attempt {retryAttempt}/{maxRetries})..."
                            });
                            httpRes.Dispose();
                            await Task.Delay(delayMs, ct);
                            continue;
                        }

                        await onEvent(new TrajectoryEventPayload
                        {
                            EventType = "response",
                            Content = $"❌ OpenRouter error (HTTP {statusCode} {httpRes.ReasonPhrase}): {FormatOpenRouterError(errContent)}"
                        });
                        httpRes.Dispose();
                        return;
                    }

                    break;
                }

                using var activeResponse = httpRes!;
                var firstChunkReceived = false;
                var chunkIdleTimeout = TimeSpan.FromSeconds(streamChunkIdleTimeoutSeconds);

                using var prefillLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, outerRequestCts!.Token);
                using var stream = await activeResponse.Content.ReadAsStreamAsync(prefillLinkedCts.Token);
                using var reader = new StreamReader(stream);

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
                            Content = $"[@{preset.Id}] SSE stream stalled for {streamChunkIdleTimeoutSeconds}s after activity; cancelling and retrying..."
                        });
                        streamDropped = new TimeoutException($"SSE stream stalled after {streamChunkIdleTimeoutSeconds}s of inactivity.");
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
                    catch (Exception ex) when (ex is IOException or HttpRequestException)
                    {
                        streamDropped = ex;
                        break;
                    }

                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var trimmed = line.Trim();

                    // OpenRouter emits SSE comment lines (": OPENROUTER PROCESSING") while queued/reasoning.
                    // Treat them as keepalive liveness so long reasoning prefills do not trip the idle watchdog.
                    if (trimmed.StartsWith(':'))
                    {
                        firstChunkReceived = true;
                        continue;
                    }

                    if (trimmed.StartsWith('{'))
                    {
                        firstChunkReceived = true;
                        using var restCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        restCts.CancelAfter(chunkIdleTimeout);
                        var rest = await reader.ReadToEndAsync(restCts.Token);
                        var fullJson = trimmed + "\n" + rest;
                        try
                        {
                            using var doc = JsonDocument.Parse(fullJson);
                            if (TryExtractStreamError(doc.RootElement, out var inlineErr))
                            {
                                streamErrorMessage = inlineErr;
                                break;
                            }
                            ParseOpenRouterChunkOrMessage(
                                doc.RootElement,
                                contentBuilder,
                                reasoningBuilder,
                                reasoningDetailsAcc,
                                toolCallsByIndex,
                                stepUsage);
                        }
                        catch { }
                        break;
                    }

                    if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

                    var data = trimmed.Length > 5 ? trimmed[5..].Trim() : string.Empty;
                    firstChunkReceived = true;
                    if (data == "[DONE]") break;

                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        if (TryExtractStreamError(doc.RootElement, out var inlineErr))
                        {
                            streamErrorMessage = inlineErr;
                            break;
                        }
                        ParseOpenRouterChunkOrMessage(
                            doc.RootElement,
                            contentBuilder,
                            reasoningBuilder,
                            reasoningDetailsAcc,
                            toolCallsByIndex,
                            stepUsage);
                    }
                    catch
                    {
                        // Ignore malformed SSE frame
                    }
                }
            }
            finally
            {
                outerRequestCts?.Dispose();
            }

            turnUsage.AddStep(stepUsage);

            if (!string.IsNullOrWhiteSpace(streamErrorMessage))
            {
                await onEvent(new TrajectoryEventPayload
                {
                    EventType = "response",
                    Content = $"❌ OpenRouter stream error: {streamErrorMessage}"
                });
                return;
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
                    Content = $"❌ Response stream from {chatUrl} ended prematurely ({streamDropped.Message}) after {streamRetryAttempt} retries."
                });
                return;
            }
            streamRetryAttempt = 0;

            var rawFinalContent = contentBuilder.ToString();
            var (inlineThought, cleanedFinalContent) = AgentToolExecutor.ExtractInlineThinkBlocks(rawFinalContent);
            if (!string.IsNullOrWhiteSpace(inlineThought))
            {
                if (reasoningBuilder.Length == 0)
                    reasoningBuilder.Append(inlineThought);
                rawFinalContent = cleanedFinalContent;
            }
            var finalContent = rawFinalContent;
            var capturedReasoningDetails = reasoningDetailsAcc.Build();

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
                await onEvent(new TrajectoryEventPayload
                {
                    EventType = "thought",
                    Content = finalContent.Trim()
                });
            }

            if (toolCallsByIndex.Count > 0)
            {
                var executedToolCalls = new List<OpenRouterToolCall>();
                var toolResults = new List<(string CallId, string Output)>();
                var checklistRegistered = false;
                var prOpened = false;

                foreach (var kvp in toolCallsByIndex)
                {
                    var callId = kvp.Value.Id ?? $"call_{Guid.NewGuid():N}";
                    var toolName = kvp.Value.Name.ToString().Trim();
                    var toolArgs = kvp.Value.Args.ToString().Trim();

                    executedToolCalls.Add(new OpenRouterToolCall
                    {
                        Id = callId,
                        Type = "function",
                        Function = new OpenRouterFunctionCall { Name = toolName, Arguments = toolArgs }
                    });

                    if (string.Equals(toolName, "ask_question", StringComparison.OrdinalIgnoreCase))
                    {
                        askedQuestion = true;
                        responseEmitted = true;
                        if (!string.IsNullOrWhiteSpace(finalContent))
                        {
                            await onEvent(new TrajectoryEventPayload { EventType = "response", Content = finalContent });
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

                    var summary = AgentToolExecutor.FormatToolSummary(toolName, toolArgs, mcpManager);
                    lastActionSummary = summary;
                    AgentToolExecutor.TrackToolActivity(toolName, toolArgs, filesInspected, filesModified, commandsRun);
                    await onEvent(new TrajectoryEventPayload
                    {
                        EventType = "tool_call",
                        ToolName = toolName,
                        Content = summary,
                        ToolArgsJson = toolArgs
                    });

                    var toolResult = await AgentToolExecutor.ExecuteBridgedOrLocalToolAsync(
                        toolName,
                        toolArgs,
                        cwd,
                        mcpManager,
                        preset,
                        promptEvent,
                        callId,
                        isReadOnlyPhase,
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

                    if (string.Equals(toolName, "post_job_message", StringComparison.OrdinalIgnoreCase) &&
                        AgentToolExecutor.IsRequiresReply(toolArgs) &&
                        !AgentToolExecutor.IsToolResultError(toolResult))
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

                    if (string.Equals(toolName, "open_pull_request", StringComparison.OrdinalIgnoreCase) &&
                        !AgentToolExecutor.IsToolResultError(toolResult))
                    {
                        responseEmitted = true;
                        prOpened = true;
                        break;
                    }

                    if (string.Equals(toolName, "manage_task", StringComparison.OrdinalIgnoreCase) &&
                        AgentToolExecutor.IsSetChecklistAction(toolArgs) &&
                        !AgentToolExecutor.IsToolResultError(toolResult))
                    {
                        checklistRegistered = true;
                    }
                }

                var assistantMsg = new OpenRouterMessage
                {
                    Role = "assistant",
                    Content = string.IsNullOrWhiteSpace(finalContent) ? null : finalContent,
                    ToolCalls = executedToolCalls,
                    ReasoningDetails = capturedReasoningDetails
                };
                history.Add(assistantMsg);

                foreach (var (cId, output) in toolResults)
                {
                    history.Add(new OpenRouterMessage
                    {
                        Role = "tool",
                        ToolCallId = cId,
                        Content = output
                    });
                }
                SaveHistoryToDisk(promptEvent.SessionId, history);

                if (prOpened)
                {
                    await onEvent(new TrajectoryEventPayload
                    {
                        EventType = "response",
                        Content = (string.IsNullOrWhiteSpace(finalContent) ? "" : finalContent.Trim() + "\n\n")
                            + "Pull request registered for this branch — turn yielded to the review gate.",
                        StructuredPayloadJson = BuildUsagePayload(effectiveModel, promptEvent, turnUsage, sw.Elapsed.TotalSeconds, estimated: false)
                    });
                    return;
                }

                if (askedQuestion)
                {
                    return;
                }

                if (checklistRegistered)
                {
                    responseEmitted = true;
                    await onEvent(new TrajectoryEventPayload
                    {
                        EventType = "response",
                        Content = !string.IsNullOrWhiteSpace(finalContent)
                            ? $"{finalContent.Trim()}\n\nTurn concluded: Living task checklist registered. Awaiting Stage 2 Plan Approval."
                            : "Turn concluded: Living task checklist registered. Awaiting Stage 2 Plan Approval.",
                        StructuredPayloadJson = BuildUsagePayload(effectiveModel, promptEvent, turnUsage, sw.Elapsed.TotalSeconds, estimated: false)
                    });
                    return;
                }

                var isPureReadOnlyTurn = executedToolCalls.All(c =>
                    AgentToolExecutor.IsReadOnlyTool(c.Function.Name, c.Function.Arguments, mcpManager));
                if (isPureReadOnlyTurn)
                {
                    readOnlyStreak++;
                    if (readOnlyStreak % 2 == 0) stepCount++;
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

                continue;
            }

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

            var effectiveContent = !string.IsNullOrWhiteSpace(finalContent)
                ? finalContent
                : (reasoningBuilder.Length > 0 ? reasoningBuilder.ToString().Trim() : "Turn completed.");

            if (toolsDefinition != null && intentNudgesUsed < 2 && AgentToolExecutor.LooksLikeUnexecutedIntent(effectiveContent))
            {
                intentNudgesUsed++;
                history.Add(new OpenRouterMessage
                {
                    Role = "assistant",
                    Content = effectiveContent,
                    ReasoningDetails = capturedReasoningDetails
                });
                history.Add(new OpenRouterMessage
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

            var usageEstimated = false;
            if (turnUsage.InputTokens == 0 && turnUsage.OutputTokens == 0)
            {
                turnUsage.InputTokens = EstimateTotalTokens(history);
                turnUsage.OutputTokens = (long)Math.Ceiling(effectiveContent.Length / 3.6);
                turnUsage.TotalTokens = turnUsage.InputTokens + turnUsage.OutputTokens;
                usageEstimated = true;
            }

            history.Add(new OpenRouterMessage
            {
                Role = "assistant",
                Content = effectiveContent,
                ReasoningDetails = capturedReasoningDetails
            });
            SaveHistoryToDisk(promptEvent.SessionId, history);

            sw.Stop();
            var structuredJson = BuildUsagePayload(effectiveModel, promptEvent, turnUsage, sw.Elapsed.TotalSeconds, usageEstimated);

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
            await EmitCheckpointSummaryAsync(
                preset, promptEvent, history, effectiveModel, chatUrl, apiKey, stickySessionId,
                timeoutSeconds, maxSteps, stepCount, filesInspected, filesModified, commandsRun,
                lastActionSummary, turnUsage, sw, onEvent, ct);
        }
    }

    private List<OpenRouterMessage> InitializeOrLoadHistory(
        AgentPresetConfig preset,
        PromptReceivedEvent promptEvent,
        string cwd,
        string effectiveModel)
    {
        var history = SessionHistories.GetOrAdd(
            promptEvent.SessionId,
            sid => LoadHistoryFromDisk(sid) ?? new List<OpenRouterMessage>());

        if (history.Count == 0)
        {
            var stack = promptEvent.PromptStack;
            var systemPrompt = HasUsableStack(stack)
                ? stack!.CachedSystemPrompt
                : AgentToolExecutor.BuildDefaultSystemPrompt(preset, promptEvent, cwd);

            history.Add(new OpenRouterMessage { Role = "system", Content = systemPrompt });

            if (HasUsableStack(stack))
            {
                foreach (var message in stack!.History ?? Array.Empty<TurnHistoryMessage>())
                {
                    if (string.IsNullOrWhiteSpace(message.Content)) continue;

                    var role = message.Role?.ToLowerInvariant() switch
                    {
                        "assistant" => "assistant",
                        "user" => "user",
                        _ => "user"
                    };
                    history.Add(new OpenRouterMessage { Role = role, Content = message.Content });
                }
            }

            SaveHistoryToDisk(promptEvent.SessionId, history);
        }

        var attachedBlock = AgentAdapterFactory.FormatAttachedFilesBlock(promptEvent.Attachments);
        var stackForUserTurn = promptEvent.PromptStack;
        var promptBody = HasUsableStack(stackForUserTurn)
            ? stackForUserTurn!.UserTaskEnvelope
            : promptEvent.Prompt;

        var fullUserPrompt = string.IsNullOrWhiteSpace(attachedBlock)
            ? promptBody
            : $"{attachedBlock}{promptBody}";

        var imageParts = LoadAttachmentImageParts(promptEvent.Attachments, cwd);
        if (imageParts.Count > 0)
        {
            var contentParts = new List<object>
            {
                new OpenRouterTextContentPart { Type = "text", Text = fullUserPrompt }
            };
            contentParts.AddRange(imageParts);

            history.Add(new OpenRouterMessage
            {
                Role = "user",
                Content = fullUserPrompt,
                ContentParts = contentParts
            });
        }
        else
        {
            history.Add(new OpenRouterMessage { Role = "user", Content = fullUserPrompt });
        }

        SaveHistoryToDisk(promptEvent.SessionId, history);
        return history;
    }

    private static List<object> LoadAttachmentImageParts(List<AttachmentItem>? attachments, string cwd)
    {
        var imageParts = new List<object>();
        if (attachments == null || attachments.Count == 0) return imageParts;

        foreach (var att in attachments)
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

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) continue;

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".webp")) continue;

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
                var base64 = Convert.ToBase64String(File.ReadAllBytes(filePath));
                imageParts.Add(new OpenRouterImageUrlContentPart
                {
                    Type = "image_url",
                    ImageUrl = new OpenRouterImageUrl { Url = $"data:{mime};base64,{base64}" }
                });
            }
            catch { }
        }

        return imageParts;
    }

    private static (bool IsReadOnlyPhase, bool IsDirectMode) ResolveExecutionPhase(PromptReceivedEvent promptEvent)
    {
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

        var isPlanMode = string.Equals(promptEvent.Mode, "plan", StringComparison.OrdinalIgnoreCase);
        var isDirectMode = string.Equals(promptEvent.Mode, "direct", StringComparison.OrdinalIgnoreCase);
        return (isDiscoveryPhase || isPlanMode, isDirectMode);
    }

    internal static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string url,
        string? apiKey,
        AgentPresetConfig preset,
        string stickySessionId,
        bool acceptSse)
    {
        var httpReq = new HttpRequestMessage(method, url);
        if (acceptSse)
            httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (!string.IsNullOrWhiteSpace(apiKey))
            httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var appTitle = !string.IsNullOrWhiteSpace(preset.OpenRouter?.AppTitle)
            ? preset.OpenRouter!.AppTitle!.Trim()
            : OpenRouterConfig.DefaultAppTitle;
        httpReq.Headers.TryAddWithoutValidation("X-Title", appTitle);

        if (!string.IsNullOrWhiteSpace(preset.OpenRouter?.HttpReferer))
            httpReq.Headers.TryAddWithoutValidation("HTTP-Referer", preset.OpenRouter!.HttpReferer!.Trim());

        httpReq.Headers.TryAddWithoutValidation("x-session-id", stickySessionId);

        if (preset.CustomHeaders != null)
        {
            foreach (var kvp in preset.CustomHeaders)
                httpReq.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
        }

        return httpReq;
    }

    internal static Dictionary<string, object?> BuildRequestBody(
        string effectiveModel,
        List<OpenRouterMessage> messages,
        AgentPresetConfig preset,
        PromptReceivedEvent promptEvent,
        string stickySessionId,
        List<object>? toolsDefinition,
        bool cacheLeadingSystem,
        bool reasoningRejected)
    {
        var serializedMessages = new List<object>(messages.Count);
        for (int i = 0; i < messages.Count; i++)
        {
            var isLeadingSystem = i == 0 && string.Equals(messages[i].Role, "system", StringComparison.Ordinal);
            serializedMessages.Add(messages[i].ToSerializableObject(cacheLeadingSystem && isLeadingSystem));
        }

        var reqBody = new Dictionary<string, object?>
        {
            ["model"] = effectiveModel,
            ["messages"] = serializedMessages,
            ["stream"] = true,
            ["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true },
            ["usage"] = new Dictionary<string, object> { ["include"] = true },
            ["session_id"] = stickySessionId
        };

        var fallbackModels = preset.OpenRouter?.FallbackModels?
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .ToList();
        if (fallbackModels is { Count: > 0 })
        {
            var modelChain = new List<string> { effectiveModel };
            foreach (var fb in fallbackModels)
            {
                if (!modelChain.Contains(fb, StringComparer.OrdinalIgnoreCase))
                    modelChain.Add(fb);
            }
            if (modelChain.Count > 1)
                reqBody["models"] = modelChain;
        }

        if (!reasoningRejected &&
            !string.IsNullOrWhiteSpace(promptEvent.Effort) &&
            DriverEffortContracts.Supports(DriverAdapters.OpenRouter, promptEvent.Effort))
        {
            reqBody["reasoning"] = new Dictionary<string, object>
            {
                ["effort"] = promptEvent.Effort.Trim().ToLowerInvariant()
            };
        }

        var providerDict = BuildProviderRoutingDictionary(preset.OpenRouter?.Provider);
        if (providerDict is { Count: > 0 })
        {
            reqBody["provider"] = providerDict;
        }

        if (toolsDefinition != null && toolsDefinition.Count > 0)
        {
            reqBody["tools"] = toolsDefinition;
            reqBody["tool_choice"] = "auto";
        }

        return reqBody;
    }

    internal static Dictionary<string, object>? BuildProviderRoutingDictionary(OpenRouterProviderRouting? provider)
    {
        if (provider == null) return null;

        var dict = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(provider.Sort))
            dict["sort"] = provider.Sort.Trim().ToLowerInvariant();

        AddNonEmptyStringList(dict, "order", provider.Order);
        AddNonEmptyStringList(dict, "only", provider.Only);
        AddNonEmptyStringList(dict, "ignore", provider.Ignore);

        if (provider.AllowFallbacks.HasValue)
            dict["allow_fallbacks"] = provider.AllowFallbacks.Value;
        if (!string.IsNullOrWhiteSpace(provider.DataCollection))
            dict["data_collection"] = provider.DataCollection.Trim().ToLowerInvariant();
        if (provider.Zdr.HasValue)
            dict["zdr"] = provider.Zdr.Value;
        if (provider.RequireParameters.HasValue)
            dict["require_parameters"] = provider.RequireParameters.Value;

        return dict.Count > 0 ? dict : null;

        static void AddNonEmptyStringList(Dictionary<string, object> target, string key, List<string>? values)
        {
            if (values == null) return;
            var cleaned = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
            if (cleaned.Count > 0) target[key] = cleaned;
        }
    }

    private static bool LooksLikeMultipartSystemRejection(string errContent)
    {
        if (string.IsNullOrWhiteSpace(errContent)) return false;
        return errContent.Contains("cache_control", StringComparison.OrdinalIgnoreCase) ||
               (errContent.Contains("content", StringComparison.OrdinalIgnoreCase) &&
                (errContent.Contains("expected string", StringComparison.OrdinalIgnoreCase) ||
                 errContent.Contains("must be a string", StringComparison.OrdinalIgnoreCase) ||
                 errContent.Contains("string, got array", StringComparison.OrdinalIgnoreCase) ||
                 errContent.Contains("invalid_type", StringComparison.OrdinalIgnoreCase)));
    }

    private static string FormatOpenRouterError(string errContent)
    {
        if (string.IsNullOrWhiteSpace(errContent)) return "Empty error response";
        try
        {
            using var doc = JsonDocument.Parse(errContent);
            if (TryExtractStreamError(doc.RootElement, out var msg) && !string.IsNullOrWhiteSpace(msg))
                return msg;
        }
        catch { }
        return errContent;
    }

    private static bool TryExtractStreamError(JsonElement root, out string message)
    {
        message = string.Empty;
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("error", out var errElem) || errElem.ValueKind == JsonValueKind.Null)
            return false;

        if (errElem.ValueKind == JsonValueKind.String)
        {
            message = errElem.GetString() ?? "Unknown OpenRouter error";
            return !string.IsNullOrWhiteSpace(message);
        }

        if (errElem.ValueKind == JsonValueKind.Object)
        {
            var msg = errElem.TryGetProperty("message", out var mProp) && mProp.ValueKind == JsonValueKind.String
                ? mProp.GetString()
                : null;
            var code = errElem.TryGetProperty("code", out var cProp)
                ? (cProp.ValueKind == JsonValueKind.String ? cProp.GetString() : cProp.GetRawText())
                : null;
            string? rawDetail = null;
            if (errElem.TryGetProperty("metadata", out var metaProp) &&
                metaProp.ValueKind == JsonValueKind.Object &&
                metaProp.TryGetProperty("raw", out var rawProp) &&
                rawProp.ValueKind == JsonValueKind.String)
            {
                rawDetail = rawProp.GetString();
            }

            var baseMsg = !string.IsNullOrWhiteSpace(msg) ? msg! : errElem.GetRawText();
            if (!string.IsNullOrWhiteSpace(code)) baseMsg = $"[{code}] {baseMsg}";
            if (!string.IsNullOrWhiteSpace(rawDetail)) baseMsg = $"{baseMsg} ({rawDetail})";
            message = baseMsg;
            return true;
        }

        return false;
    }

    internal static void ParseOpenRouterChunkOrMessage(
        JsonElement root,
        StringBuilder contentBuilder,
        StringBuilder reasoningBuilder,
        OpenRouterReasoningDetailsAccumulator reasoningDetailsAcc,
        SortedDictionary<int, (string? Id, StringBuilder Name, StringBuilder Args)> toolCallsByIndex,
        StepUsageCapture stepUsage)
    {
        if (root.TryGetProperty("usage", out var usageElem) && usageElem.ValueKind == JsonValueKind.Object)
        {
            if ((usageElem.TryGetProperty("prompt_tokens", out var pt) || usageElem.TryGetProperty("input_tokens", out pt)) &&
                pt.ValueKind == JsonValueKind.Number && pt.TryGetInt64(out var ptVal))
            {
                stepUsage.InputTokens = ptVal;
            }
            if ((usageElem.TryGetProperty("completion_tokens", out var ctProp) || usageElem.TryGetProperty("output_tokens", out ctProp)) &&
                ctProp.ValueKind == JsonValueKind.Number && ctProp.TryGetInt64(out var ctVal))
            {
                stepUsage.OutputTokens = ctVal;
            }
            if (usageElem.TryGetProperty("total_tokens", out var tt) &&
                tt.ValueKind == JsonValueKind.Number && tt.TryGetInt64(out var ttVal))
            {
                stepUsage.TotalTokens = ttVal;
            }
            if (usageElem.TryGetProperty("completion_tokens_details", out var ctd) &&
                ctd.ValueKind == JsonValueKind.Object &&
                ctd.TryGetProperty("reasoning_tokens", out var rt) &&
                rt.ValueKind == JsonValueKind.Number && rt.TryGetInt64(out var rtVal))
            {
                stepUsage.ThinkingTokens = rtVal;
            }
            if (usageElem.TryGetProperty("prompt_tokens_details", out var ptd) &&
                ptd.ValueKind == JsonValueKind.Object &&
                ptd.TryGetProperty("cached_tokens", out var cached) &&
                cached.ValueKind == JsonValueKind.Number && cached.TryGetInt64(out var cachedVal))
            {
                stepUsage.CachedTokens = cachedVal;
            }
            if (usageElem.TryGetProperty("cost", out var costProp))
            {
                if (costProp.ValueKind == JsonValueKind.Number && costProp.TryGetDouble(out var cVal) && cVal >= 0)
                    stepUsage.CostUsd = cVal;
                else if (costProp.ValueKind == JsonValueKind.String &&
                         double.TryParse(costProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedCost) &&
                         parsedCost >= 0)
                    stepUsage.CostUsd = parsedCost;
            }
        }

        if (!root.TryGetProperty("choices", out var choicesElem) ||
            choicesElem.ValueKind != JsonValueKind.Array ||
            choicesElem.GetArrayLength() == 0)
        {
            return;
        }

        var choice = choicesElem[0];
        var isDelta = choice.TryGetProperty("delta", out var deltaElem);
        var elem = isDelta ? deltaElem : (choice.TryGetProperty("message", out var msgElem) ? msgElem : choice);

        if (elem.TryGetProperty("content", out var cProp) && cProp.ValueKind == JsonValueKind.String)
        {
            var textChunk = cProp.GetString();
            if (!string.IsNullOrEmpty(textChunk))
                contentBuilder.Append(textChunk);
        }

        var sawStringReasoning = false;
        if ((elem.TryGetProperty("reasoning", out var rProp) ||
             elem.TryGetProperty("reasoning_content", out rProp) ||
             elem.TryGetProperty("thought", out rProp) ||
             elem.TryGetProperty("reasoning_text", out rProp)) &&
            rProp.ValueKind == JsonValueKind.String)
        {
            var reasoningChunk = rProp.GetString();
            if (!string.IsNullOrEmpty(reasoningChunk))
            {
                reasoningBuilder.Append(reasoningChunk);
                sawStringReasoning = true;
            }
        }

        if (elem.TryGetProperty("reasoning_details", out var rdProp) && rdProp.ValueKind == JsonValueKind.Array)
        {
            reasoningDetailsAcc.MergeArray(rdProp, isDelta, emitTextTo: sawStringReasoning ? null : reasoningBuilder);
        }

        if (elem.TryGetProperty("tool_calls", out var tcProp) && tcProp.ValueKind == JsonValueKind.Array)
        {
            int fallbackIdx = 0;
            foreach (var tcElem in tcProp.EnumerateArray())
            {
                var idx = tcElem.TryGetProperty("index", out var iProp) && iProp.ValueKind == JsonValueKind.Number
                    ? iProp.GetInt32()
                    : fallbackIdx++;

                if (!toolCallsByIndex.TryGetValue(idx, out var tuple))
                {
                    tuple = (null, new StringBuilder(), new StringBuilder());
                    toolCallsByIndex[idx] = tuple;
                }

                if (tcElem.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                    tuple.Id = idProp.GetString();

                if (tcElem.TryGetProperty("function", out var fnElem))
                {
                    if (fnElem.TryGetProperty("name", out var fnNameProp) && fnNameProp.ValueKind == JsonValueKind.String)
                        tuple.Name.Append(fnNameProp.GetString());
                    if (fnElem.TryGetProperty("arguments", out var fnArgsProp) && fnArgsProp.ValueKind == JsonValueKind.String)
                        tuple.Args.Append(fnArgsProp.GetString());
                }

                toolCallsByIndex[idx] = tuple;
            }
        }
    }

    private static string? BuildUsagePayload(
        string effectiveModel,
        PromptReceivedEvent promptEvent,
        TurnUsageAccumulator turnUsage,
        double durationSeconds,
        bool estimated)
    {
        var total = turnUsage.TotalTokens > 0
            ? turnUsage.TotalTokens
            : turnUsage.InputTokens + turnUsage.OutputTokens;

        if (total <= 0 && durationSeconds <= 0) return null;

        return JsonSerializer.Serialize(new
        {
            model = effectiveModel,
            effort = promptEvent.Effort,
            mode = promptEvent.Mode,
            contextSize = promptEvent.ContextSize,
            usage = new
            {
                inputTokens = turnUsage.InputTokens,
                outputTokens = turnUsage.OutputTokens,
                thinkingTokens = turnUsage.ThinkingTokens,
                cachedTokens = turnUsage.CachedTokens,
                totalTokens = total,
                costUsd = Math.Round(turnUsage.CostUsd, 8),
                durationSeconds,
                estimated
            }
        });
    }

    private static async Task EmitCheckpointSummaryAsync(
        AgentPresetConfig preset,
        PromptReceivedEvent promptEvent,
        List<OpenRouterMessage> history,
        string effectiveModel,
        string chatUrl,
        string apiKey,
        string stickySessionId,
        int timeoutSeconds,
        int maxSteps,
        int stepCount,
        HashSet<string> filesInspected,
        HashSet<string> filesModified,
        List<string> commandsRun,
        string? lastActionSummary,
        TurnUsageAccumulator turnUsage,
        Stopwatch sw,
        Func<TrajectoryEventPayload, Task> onEvent,
        CancellationToken ct)
    {
        await onEvent(new TrajectoryEventPayload
        {
            EventType = "thought",
            Content = $"[@{preset.Id}] Reached progress checkpoint ({maxSteps} steps). Generating status summary..."
        });

        string? summaryContent = null;
        try
        {
            var summaryHistory = new List<OpenRouterMessage>(history)
            {
                new OpenRouterMessage
                {
                    Role = "user",
                    Content = $"[System Notice: You have reached the {maxSteps}-step progress checkpoint for this turn. Please summarize what you have accomplished so far, the current status of your changes, and what next steps remain.]"
                }
            };

            var summaryReqBody = new Dictionary<string, object?>
            {
                ["model"] = effectiveModel,
                ["messages"] = summaryHistory.Select(m => m.ToSerializableObject(cacheSystem: false)).ToList(),
                ["stream"] = false,
                ["session_id"] = stickySessionId
            };

            using var sumHttpReq = CreateRequest(HttpMethod.Post, chatUrl, apiKey, preset, stickySessionId, acceptSse: false);
            sumHttpReq.Content = new StringContent(JsonSerializer.Serialize(summaryReqBody), Encoding.UTF8, "application/json");

            using var sumCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(120, timeoutSeconds)));
            using var sumLinked = CancellationTokenSource.CreateLinkedTokenSource(ct, sumCts.Token);

            using var sumRes = await HttpClientInstance.SendAsync(sumHttpReq, sumLinked.Token);
            if (sumRes.IsSuccessStatusCode)
            {
                var sumJson = await sumRes.Content.ReadAsStringAsync(sumLinked.Token);
                using var sumDoc = JsonDocument.Parse(sumJson);
                if (sumDoc.RootElement.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0 &&
                    ch[0].TryGetProperty("message", out var msgElem) &&
                    msgElem.TryGetProperty("content", out var cVal) &&
                    cVal.ValueKind == JsonValueKind.String)
                {
                    summaryContent = cVal.GetString();
                }
            }
        }
        catch { }

        var detailsSb = new StringBuilder();
        detailsSb.AppendLine($"🔄 **Progress Checkpoint ({maxSteps} steps)**:\n");
        var agentTag = !string.IsNullOrWhiteSpace(promptEvent.PersonaId) ? promptEvent.PersonaId : preset.Id;
        var personaDisplay = !string.IsNullOrWhiteSpace(promptEvent.PersonaName) ? $" ({promptEvent.PersonaName})" : "";
        detailsSb.AppendLine($"Agent `@{agentTag}`{personaDisplay} reached the {maxSteps}-step progress limit for this turn and is rolling over.\n");

        var goal = AgentToolExecutor.ExtractGoal(promptEvent);
        if (!string.IsNullOrWhiteSpace(goal))
            detailsSb.AppendLine($"**Active Goal**: {goal}\n");

        if (!string.IsNullOrWhiteSpace(summaryContent))
        {
            detailsSb.AppendLine(summaryContent.Trim());
            detailsSb.AppendLine();
        }
        else if (!string.IsNullOrWhiteSpace(lastActionSummary))
        {
            detailsSb.AppendLine($"**Current Activity**: {lastActionSummary}\n");
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

        var estimated = false;
        if (turnUsage.InputTokens == 0 && turnUsage.OutputTokens == 0)
        {
            turnUsage.InputTokens = EstimateTotalTokens(history);
            turnUsage.OutputTokens = (long)Math.Ceiling(finalMsg.Length / 3.6);
            turnUsage.TotalTokens = turnUsage.InputTokens + turnUsage.OutputTokens;
            estimated = true;
        }

        history.Add(new OpenRouterMessage { Role = "assistant", Content = finalMsg });
        SaveHistoryToDisk(promptEvent.SessionId, history);

        await onEvent(new TrajectoryEventPayload
        {
            EventType = "response",
            Content = finalMsg,
            StructuredPayloadJson = BuildUsagePayload(effectiveModel, promptEvent, turnUsage, sw.Elapsed.TotalSeconds, estimated)
        });
    }

    internal sealed class StepUsageCapture
    {
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long ThinkingTokens { get; set; }
        public long CachedTokens { get; set; }
        public long TotalTokens { get; set; }
        public double CostUsd { get; set; }
    }

    internal sealed class TurnUsageAccumulator
    {
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long ThinkingTokens { get; set; }
        public long CachedTokens { get; set; }
        public long TotalTokens { get; set; }
        public double CostUsd { get; set; }

        public void AddStep(StepUsageCapture step)
        {
            InputTokens += step.InputTokens;
            OutputTokens += step.OutputTokens;
            ThinkingTokens += step.ThinkingTokens;
            CachedTokens += step.CachedTokens;
            TotalTokens += step.TotalTokens > 0 ? step.TotalTokens : step.InputTokens + step.OutputTokens;
            CostUsd += step.CostUsd;
        }
    }

    /// <summary>
    /// Accumulates OpenRouter <c>reasoning_details</c> chunks across SSE frames into complete,
    /// verbatim JSON blocks ready to replay in subsequent assistant history messages.
    /// </summary>
    internal sealed class OpenRouterReasoningDetailsAccumulator
    {
        private readonly List<(string Key, JsonObject Obj)> _entries = new();

        public void MergeArray(JsonElement array, bool isDelta, StringBuilder? emitTextTo)
        {
            var fallbackIndex = 0;
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var type = item.TryGetProperty("type", out var tProp) && tProp.ValueKind == JsonValueKind.String
                    ? tProp.GetString() ?? string.Empty
                    : string.Empty;
                var id = item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                    ? idProp.GetString()
                    : null;
                var index = item.TryGetProperty("index", out var idxProp) && idxProp.ValueKind == JsonValueKind.Number
                    ? idxProp.GetInt32()
                    : fallbackIndex;
                fallbackIndex++;

                var key = !string.IsNullOrWhiteSpace(id)
                    ? $"id:{type}:{id}"
                    : $"idx:{type}:{index}";

                var existingIdx = _entries.FindIndex(e => string.Equals(e.Key, key, StringComparison.Ordinal));
                if (existingIdx < 0)
                {
                    var cloned = JsonNode.Parse(item.GetRawText()) as JsonObject;
                    if (cloned != null)
                    {
                        _entries.Add((key, cloned));
                    }
                }
                else
                {
                    var target = _entries[existingIdx].Obj;
                    foreach (var prop in item.EnumerateObject())
                    {
                        if (isDelta && prop.Name is "text" or "summary" or "data" && prop.Value.ValueKind == JsonValueKind.String)
                        {
                            var delta = prop.Value.GetString() ?? string.Empty;
                            var current = target[prop.Name]?.GetValue<string>() ?? string.Empty;
                            target[prop.Name] = current + delta;
                        }
                        else if (prop.Value.ValueKind != JsonValueKind.Null)
                        {
                            target[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
                        }
                    }
                }

                if (emitTextTo != null)
                {
                    if (item.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
                    {
                        emitTextTo.Append(textProp.GetString());
                    }
                    else if (item.TryGetProperty("summary", out var summaryProp) && summaryProp.ValueKind == JsonValueKind.String)
                    {
                        emitTextTo.Append(summaryProp.GetString());
                    }
                }
            }
        }

        public List<JsonElement>? Build()
        {
            if (_entries.Count == 0) return null;
            var result = new List<JsonElement>(_entries.Count);
            foreach (var (_, obj) in _entries)
            {
                using var doc = JsonDocument.Parse(obj.ToJsonString());
                result.Add(doc.RootElement.Clone());
            }
            return result;
        }
    }
}

public class OpenRouterMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }

    [JsonIgnore]
    public List<object>? ContentParts { get; set; }

    [JsonPropertyName("reasoning_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<JsonElement>? ReasoningDetails { get; set; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OpenRouterToolCall>? ToolCalls { get; set; }

    public OpenRouterMessage Clone() => new()
    {
        Role = Role,
        Content = Content,
        ContentParts = ContentParts,
        ReasoningDetails = ReasoningDetails,
        ToolCallId = ToolCallId,
        ToolCalls = ToolCalls
    };

    public object ToSerializableObject(bool cacheSystem = false)
    {
        var dict = new Dictionary<string, object?> { ["role"] = Role };

        if (cacheSystem && string.Equals(Role, "system", StringComparison.Ordinal) && !string.IsNullOrEmpty(Content))
        {
            dict["content"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = Content,
                    ["cache_control"] = new Dictionary<string, string> { ["type"] = "ephemeral" }
                }
            };
        }
        else if (ContentParts != null && ContentParts.Count > 0)
        {
            dict["content"] = ContentParts;
        }
        else if (Content != null)
        {
            dict["content"] = Content;
        }

        if (!string.IsNullOrEmpty(ToolCallId))
            dict["tool_call_id"] = ToolCallId;

        if (ReasoningDetails != null && ReasoningDetails.Count > 0)
            dict["reasoning_details"] = ReasoningDetails;

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

public class OpenRouterTextContentPart
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

public class OpenRouterImageUrlContentPart
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "image_url";

    [JsonPropertyName("image_url")]
    public OpenRouterImageUrl ImageUrl { get; set; } = new();
}

public class OpenRouterImageUrl
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

public class OpenRouterToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public OpenRouterFunctionCall Function { get; set; } = new();
}

public class OpenRouterFunctionCall
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;
}
