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

namespace AgentMesh.Cli.Adapters;

public class OpenAiAdapter : IAgentAdapter
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

    public static void ClearSessionHistory(Guid sessionId)
    {
        SessionHistories.TryRemove(sessionId, out _);
    }

    public static List<OpenAiMessage>? GetSessionHistory(Guid sessionId)
    {
        return SessionHistories.TryGetValue(sessionId, out var history) ? history : null;
    }

    public static int ResolveTimeoutSeconds(AgentPresetConfig preset)
    {
        if (preset.TimeoutSeconds.HasValue && preset.TimeoutSeconds.Value > 0)
            return preset.TimeoutSeconds.Value;

        if (int.TryParse(Environment.GetEnvironmentVariable("OPENAI_TIMEOUT_SECONDS"), out var envSec) && envSec > 0)
            return envSec;

        return 600; // 10 minutes default (increased from 100s / 5m)
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

    public static int ResolveModelContextLimit(string model, string? contextSizeOption)
    {
        if (!string.IsNullOrWhiteSpace(contextSizeOption) && !contextSizeOption.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            var normalized = contextSizeOption.Trim().ToLowerInvariant();
            if (normalized.EndsWith("k") && int.TryParse(normalized[..^1], out var k))
                return k * 1024;
            if (normalized.EndsWith("m") && int.TryParse(normalized[..^1], out var m))
                return m * 1024 * 1024;
            if (int.TryParse(normalized, out var exact))
                return exact;
        }

        var mLower = model.ToLowerInvariant();
        if (mLower.Contains("qwen3") || mLower.Contains("qwen-3")) return 524_288; // 512k tokens
        if (mLower.Contains("qwen2.5") || mLower.Contains("qwen-2.5")) return 131_072; // 128k
        if (mLower.Contains("llama-3") || mLower.Contains("llama3")) return 131_072; // 128k
        if (mLower.Contains("gpt-4o")) return 131_072; // 128k
        if (mLower.Contains("deepseek")) return 131_072; // 128k

        return 131_072; // 128k safe default
    }

    public static int EstimateMessageTokens(OpenAiMessage message)
    {
        var charCount = (message.Role?.Length ?? 0) + (message.Content?.Length ?? 0);
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

    public static bool CompactHistory(List<OpenAiMessage> history, int targetMaxTokens, bool forceAggressive = false)
    {
        if (history.Count <= 1) return false;

        var modified = false;
        var protectedTailCount = forceAggressive ? 2 : 4;
        var cutoff = Math.Max(1, history.Count - protectedTailCount);

        // Phase 1: Truncate older tool output messages
        for (int i = 1; i < cutoff; i++)
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

        // Phase 2: If still over limit, slide older message turns atomically
        var prunedTurns = 0;
        while (history.Count > (protectedTailCount + 1) && EstimateTotalTokens(history) > targetMaxTokens)
        {
            var msg1 = history[1];
            if (msg1.Role == "user")
            {
                history.RemoveAt(1);
                prunedTurns++;
                modified = true;
            }
            else if (msg1.Role == "assistant")
            {
                history.RemoveAt(1);
                modified = true;
                while (history.Count > 1 && history[1].Role == "tool")
                {
                    history.RemoveAt(1);
                }
                prunedTurns++;
            }
            else
            {
                history.RemoveAt(1);
                modified = true;
            }
        }

        if (prunedTurns > 0)
        {
            history.Insert(1, new OpenAiMessage
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

        var apiKey = !string.IsNullOrWhiteSpace(preset.ApiKey)
            ? preset.ApiKey.Trim()
            : Environment.GetEnvironmentVariable("OPENAI_API_KEY");

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
        var history = SessionHistories.GetOrAdd(promptEvent.SessionId, _ => new List<OpenAiMessage>());
        if (history.Count == 0)
        {
            var systemPrompt = BuildSystemPrompt(preset, promptEvent, cwd);
            history.Add(new OpenAiMessage { Role = "system", Content = systemPrompt });
        }

        var attachedBlock = AgentAdapterFactory.FormatAttachedFilesBlock(promptEvent.Attachments);
        var fullUserPrompt = string.IsNullOrWhiteSpace(attachedBlock)
            ? promptEvent.Prompt
            : $"{attachedBlock}{promptEvent.Prompt}";

        history.Add(new OpenAiMessage { Role = "user", Content = fullUserPrompt });

        var isDirectMode = string.Equals(promptEvent.Mode, "direct", StringComparison.OrdinalIgnoreCase);
        var toolsDefinition = isDirectMode ? null : GetToolDefinitions();

        var maxSteps = ResolveMaxSteps(preset);
        var stepCount = 0;
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
                var compacted = CompactHistory(history, targetMaxTokens);
                if (compacted)
                {
                    var afterEst = EstimateTotalTokens(history);
                    await onEvent(new TrajectoryEventPayload
                    {
                        EventType = "thought",
                        Content = $"[@{preset.Id}] Proactively compacted conversation history ({beforeEst} -> {afterEst} tokens) to remain within {modelContextLimit} context limit."
                    });
                }
            }

            HttpResponseMessage? httpRes = null;
            var retryAttempt = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var reqBody = new Dictionary<string, object?>
                {
                    ["model"] = effectiveModel,
                    ["messages"] = history.Select(m => m.ToSerializableObject()).ToList(),
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
                    await onEvent(new TrajectoryEventPayload
                    {
                        EventType = "response",
                        Content = $"❌ {errorMsg}\n\nPlease verify that your server is running and accessible at `{endpoint}`."
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
                        var targetTokens = (int)(effectiveLimit * 0.70);
                        await onEvent(new TrajectoryEventPayload
                        {
                            EventType = "thought",
                            Content = $"[@{preset.Id}] Context window exceeded ({promptTokens?.ToString() ?? "prompt"} tokens vs {effectiveLimit} limit). Compacting history and retrying..."
                        });

                        var compacted = CompactHistory(history, targetTokens, forceAggressive: true);
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

                    var toolResult = await ExecuteToolLocallyAsync(toolName, toolArgs, cwd, ct);

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

                var toolResult = await ExecuteToolLocallyAsync(fbName, fbArgs ?? "{}", cwd, ct);

                await onEvent(new TrajectoryEventPayload
                {
                    EventType = "tool_result",
                    ToolName = fbName,
                    Content = $"Completed {fbName}",
                    ToolResultJson = toolResult
                });

                history.Add(new OpenAiMessage { Role = "assistant", Content = finalContent });
                history.Add(new OpenAiMessage { Role = "user", Content = $"Tool Result for {fbName}:\n{toolResult}" });
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
        sb.AppendLine("- Use `read_file` to inspect code files.");
        sb.AppendLine("- Use `write_file` or `edit_file` to implement solutions.");
        sb.AppendLine("- When you need user clarification or critical architectural decisions, call `ask_question`.");
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
                    description = "Execute a shell command in the workspace directory. Use this to run builds, tests, git commands, and inspection tools.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            command = new { type = "string", description = "The exact shell command line string to execute." },
                            workingDirectory = new { type = "string", description = "Optional subdirectory or working directory. Defaults to workspace root." }
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

    public static async Task<string> ExecuteToolLocallyAsync(string toolName, string argsJson, string cwd, CancellationToken ct)
    {
        var rawResult = await ExecuteToolLocallyRawAsync(toolName, argsJson, cwd, ct);
        return TruncateToolResult(rawResult);
    }

    private static async Task<string> ExecuteToolLocallyRawAsync(string toolName, string argsJson, string cwd, CancellationToken ct)
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

                    var (exitCode, stdout, stderr) = await AgentAdapterFactory.RunShellCommandAsync(cmd, effectiveDir, ct);
                    var sb = new StringBuilder();
                    if (!string.IsNullOrWhiteSpace(stdout)) sb.AppendLine(stdout);
                    if (!string.IsNullOrWhiteSpace(stderr)) sb.AppendLine(stderr);
                    sb.AppendLine($"Exit Code: {exitCode}");
                    return sb.ToString().Trim();
                }

                case "read_file":
                case "view_file":
                {
                    var path = GetPropertyString(root, "path", "Path", "file_path", "filePath");
                    if (string.IsNullOrWhiteSpace(path)) return "Error: 'path' argument is required.";

                    var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(cwd, path);
                    if (!File.Exists(fullPath)) return $"Error: File '{path}' does not exist.";

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

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OpenAiToolCall>? ToolCalls { get; set; }

    public object ToSerializableObject()
    {
        var dict = new Dictionary<string, object?> { ["role"] = Role };
        if (Content != null) dict["content"] = Content;
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
