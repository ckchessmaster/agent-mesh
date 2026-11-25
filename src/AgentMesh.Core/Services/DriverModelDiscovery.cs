using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentMesh.Core.Services;

/// <summary>A model the driver can run, with the options that model actually accepts.</summary>
/// <param name="Efforts">
/// Effort values to offer. Only values the adapter will forward verbatim belong here; the
/// reading that carries this model records where they came from (<c>effortSource</c> on the
/// capability response).
/// </param>
/// <param name="ContextSizes">
/// Context options for this model, or null when the driver cannot be told the window size.
/// </param>
public sealed record DriverModelCapability(
    string Id,
    string Name,
    IReadOnlyList<string> Efforts,
    IReadOnlyList<string>? ContextSizes = null,
    int? MaxContextTokens = null,
    double? PromptPricePerMillion = null,
    double? CompletionPricePerMillion = null);

/// <summary>
/// A model entry extracted from an OpenAI-compatible <c>/models</c> response, including its
/// server-reported maximum context window in tokens when the provider supplies one.
/// </summary>
public sealed record DiscoveredOpenAiModel(
    string Id,
    int? MaxContextTokens = null);

/// <summary>
/// A tool-capable text model extracted from OpenRouter's <c>/api/v1/models</c> response,
/// including display name, context window, reasoning support, and per-million USD pricing.
/// </summary>
public sealed record DiscoveredOpenRouterModel(
    string Id,
    string Name,
    int? MaxContextTokens,
    IReadOnlyList<string> Efforts,
    double? PromptPricePerMillion = null,
    double? CompletionPricePerMillion = null);

/// <summary>
/// Parsing primitives for driver capability discovery, shared by the API's background probe and
/// the runner's self-declaration.
/// </summary>
/// <remarks>
/// <para>
/// These live in Core rather than in <c>AgentMesh.Api.Services</c> because option C (runner
/// declares its own capabilities) needs the exact same <c>agy models</c> and
/// <c>GET /models</c> readers on the runner side, and <c>AgentMesh.Cli</c> references only Core.
/// Duplicating the parsers would have re-created the precise defect this change removes: two
/// independent readers of one provider, disagreeing silently, with the runtime copy winning.
/// </para>
/// <para>
/// Nothing here invents a value. Every member either reads the provider's own output or reports
/// that it could not.
/// </para>
/// </remarks>
public static class DriverModelDiscovery
{
    private static readonly Regex AnsiEscapeRegex = new(
        @"\x1B\[[0-9;?]*[ -/]*[@-~]",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses <c>agy models</c> output. The CLI prints one <c>&lt;id&gt; &lt;display name&gt;</c>
    /// row per effort variant and offers no JSON mode (<c>--output-format</c> is a print-mode
    /// flag), so text parsing is the only channel available.
    /// </summary>
    /// <remarks>
    /// Effort variants are grouped back into one entry per base model: the picker selects a model
    /// and an effort separately, and <c>AgyAdapter</c> passes <c>--model</c> without the suffix.
    /// Splits on both <c>\r</c> and <c>\n</c> because <c>agy models</c> overwrites its Braille
    /// spinner line using carriage returns (<c>\r⠋ Fetching available models...\rgemini-4-argon-medium...</c>)
    /// without emitting a newline before the first model row.
    /// </remarks>
    public static IReadOnlyList<DriverModelCapability> ParseAgyModels(string raw)
    {
        var grouped = new Dictionary<string, (string DisplayName, HashSet<string> Efforts)>(
            StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var line in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var cleaned = AnsiEscapeRegex.Replace(line, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(cleaned)) continue;

            // Spinners, progress lines and the "Fetching available models..." banner.
            if (IsBrailleSpinner(cleaned[0])) continue;
            if (cleaned.StartsWith("fetching ", StringComparison.OrdinalIgnoreCase)) continue;
            if (cleaned.StartsWith("error", StringComparison.OrdinalIgnoreCase)) continue;
            if (cleaned.StartsWith("available models", StringComparison.OrdinalIgnoreCase)) continue;
            if (cleaned.StartsWith("model", StringComparison.OrdinalIgnoreCase) &&
                (cleaned.Contains("name", StringComparison.OrdinalIgnoreCase) ||
                 cleaned.Contains("id", StringComparison.OrdinalIgnoreCase)))
            {
                continue; // header row
            }

            var parts = cleaned.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            var modelId = parts[0];
            if (!LooksLikeModelId(modelId)) continue;

            var effort = ExtractEffortSuffix(modelId);
            var baseId = effort != null ? modelId[..(modelId.Length - effort.Length - 1)] : modelId;
            if (string.IsNullOrEmpty(baseId)) continue;

            var displayName = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : baseId;
            if (effort != null && displayName.EndsWith($"({effort})", StringComparison.OrdinalIgnoreCase))
            {
                displayName = displayName[..^(effort.Length + 2)].Trim();
            }

            if (!grouped.TryGetValue(baseId, out var entry))
            {
                entry = (string.IsNullOrWhiteSpace(displayName) ? baseId : displayName,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                grouped[baseId] = entry;
                order.Add(baseId);
            }

            if (effort != null) entry.Efforts.Add(effort.ToLowerInvariant());
        }

            return order
                .Select(baseId =>
                {
                    var (displayName, efforts) = grouped[baseId];
                    return new DriverModelCapability(
                        Id: baseId,
                        Name: displayName,
                        // Only efforts the binary documents AND the adapter forwards verbatim.
                        Efforts: efforts
                            .Where(AgyAdapterAcceptsEffort)
                            .OrderBy(EffortOrder)
                            .ToList(),
                        // agy has no context-size flag; per-model sizes used to be invented by
                        // substring-matching the model id.
                        ContextSizes: null);
                })
                .ToList();
        }

        private static bool IsBrailleSpinner(char c) => c >= '\u2800' && c <= '\u28FF';

        /// <summary>
        /// Whether a token is shaped like a model id rather than a word of prose.
        /// </summary>
        /// <remarks>
        /// <c>agy models</c> has no machine-readable mode, so this is text parsing and it will
        /// eventually meet an output format we have not seen. The failure modes are not symmetric:
        /// accepting a prose line invents a model the user can select and then send to the driver,
        /// while rejecting a real one yields an empty list plus a reason the UI displays. So the rule
        /// is strict - an id must be long, made only of id characters, and carry a version separator
        /// ('-', '.' or a digit), which is true of every id shape observed from the drivers we target
        /// (<c>gemini-4-argon-high</c>, <c>gpt-4o</c>, <c>claude-sonnet-5-5</c>, <c>llama3.3</c>).
        /// </remarks>
        public static bool LooksLikeModelId(string token)
        {
            if (token.Length < 4) return false;

            // Ids start and end with an identifier character. This is what rejects "Error:" and
            // "models:" while keeping "llama3.1:8b", whose colon is part of a real ollama tag.
            if (!char.IsAsciiLetterOrDigit(token[0]) || !char.IsAsciiLetterOrDigit(token[^1])) return false;

            var hasSeparator = false;
            foreach (var c in token)
            {
                if (char.IsAsciiLetterOrDigit(c))
                {
                    // A digit inside an id ("gpt-4o", "llama3.3") is a version marker, which is the
                    // other way a real id distinguishes itself from an English word.
                    if (char.IsAsciiDigit(c)) hasSeparator = true;
                    continue;
                }

                if (c is '-' or '.' or '_' or ':')
                {
                    hasSeparator = true;
                    continue;
                }

                return false; // prose carries punctuation we do not expect in an id
            }

            return hasSeparator;
        }

        public static string? ExtractEffortSuffix(string id)
        {
            foreach (var suffix in new[] { "-low", "-medium", "-high", "-xhigh", "-max" })
            {
                if (id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return suffix[1..].ToLowerInvariant();
            }

            return null;
        }

        /// <summary>
        /// The effort set <c>agy --help</c> documents for <c>--effort</c>
        /// (<c>low|medium|high|xhigh|max</c>).
        /// </summary>
        /// <remarks>
        /// A model id ending in <c>-nano</c> or <c>-beta</c> is a variant, not an effort; stripping it
        /// would invent a model the CLI never offered.
        /// </remarks>
        public static bool AgyAdapterAcceptsEffort(string effort) =>
            effort.ToLowerInvariant() is "low" or "medium" or "high" or "xhigh" or "max";

        private static int EffortOrder(string effort) => effort.ToLowerInvariant() switch
        {
            "low" => 1,
            "medium" => 2,
            "high" => 3,
            "xhigh" => 4,
            "max" => 5,
            _ => 99
        };
    /// <summary>
    /// Extracts <c>data[].id</c> from a <c>/models</c> payload. Returns an empty list for any body
    /// that is not the expected shape - a gateway returning HTML has no models, and pretending
    /// otherwise would render an empty picker as a successful probe.
    /// </summary>
    public static IReadOnlyList<string> ParseOpenAiModelIds(string json)
        => ParseOpenAiModels(json).Select(m => m.Id).ToList();

    /// <summary>
    /// Extracts model entries (id plus optional provider-reported context window limit) from an
    /// OpenAI-compatible <c>/models</c> payload.
    /// </summary>
    public static IReadOnlyList<DiscoveredOpenAiModel> ParseOpenAiModels(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            JsonElement data;
            if (!TryGetPropertyCaseInsensitive(doc.RootElement, "data", out data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                if (!TryGetPropertyCaseInsensitive(doc.RootElement, "models", out data) ||
                    data.ValueKind != JsonValueKind.Array)
                {
                    return Array.Empty<DiscoveredOpenAiModel>();
                }
            }

            var models = new List<DiscoveredOpenAiModel>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                string? modelId = null;
                if (TryGetPropertyCaseInsensitive(item, "id", out var id) &&
                    id.ValueKind == JsonValueKind.String)
                {
                    modelId = id.GetString();
                }
                else if (TryGetPropertyCaseInsensitive(item, "name", out var nameProp) &&
                         nameProp.ValueKind == JsonValueKind.String)
                {
                    modelId = nameProp.GetString();
                }

                if (string.IsNullOrWhiteSpace(modelId)) continue;

                var maxContext = ExtractModelContextLimit(item);
                models.Add(new DiscoveredOpenAiModel(modelId!.Trim(), maxContext));
            }

            return models;
        }
        catch (JsonException)
        {
            return Array.Empty<DiscoveredOpenAiModel>();
        }
    }

    /// <summary>
    /// Context tiers supported by GitHub Copilot CLI via <c>--context</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> CopilotContextSizes =
        new[] { "default", "long_context", "128k", "200k", "1m" };

    /// <summary>
    /// Extracts model capabilities from a GitHub Copilot <c>/models</c> payload.
    /// Preserves or prepends the "auto" model option supported by the Copilot CLI.
    /// </summary>
    public static IReadOnlyList<DriverModelCapability> ParseCopilotModels(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            JsonElement data;
            if (!TryGetPropertyCaseInsensitive(doc.RootElement, "data", out data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                if (!TryGetPropertyCaseInsensitive(doc.RootElement, "models", out data) ||
                    data.ValueKind != JsonValueKind.Array)
                {
                    return Array.Empty<DriverModelCapability>();
                }
            }

            var result = new List<DriverModelCapability>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasAuto = false;

            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                string? modelId = null;
                if (TryGetPropertyCaseInsensitive(item, "id", out var idProp) &&
                    idProp.ValueKind == JsonValueKind.String)
                {
                    modelId = idProp.GetString()?.Trim();
                }

                if (string.IsNullOrWhiteSpace(modelId)) continue;

                string displayName = modelId;
                if (TryGetPropertyCaseInsensitive(item, "name", out var nameProp) &&
                    nameProp.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(nameProp.GetString()))
                {
                    displayName = nameProp.GetString()!.Trim();
                }

                if (!seen.Add(modelId)) continue;

                if (string.Equals(modelId, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    hasAuto = true;
                    displayName = "Auto (Copilot picks the model)";
                }

                var maxContext = ExtractModelContextLimit(item);
                if (maxContext is > 0)
                {
                    ModelContextLimits.RecordLearnedLimit(modelId, maxContext.Value);
                }

                result.Add(new DriverModelCapability(
                    Id: modelId,
                    Name: displayName,
                    Efforts: Array.Empty<string>(),
                    ContextSizes: CopilotContextSizes));
            }

            if (!hasAuto && result.Count > 0)
            {
                result.Insert(0, new DriverModelCapability(
                    Id: "auto",
                    Name: "Auto (Copilot picks the model)",
                    Efforts: Array.Empty<string>(),
                    ContextSizes: CopilotContextSizes));
            }

            return result;
        }
        catch (JsonException)
        {
            return Array.Empty<DriverModelCapability>();
        }
    }

    /// <summary>
    /// Extracts tool-capable text models from OpenRouter's <c>GET /api/v1/models</c> payload,
    /// preserving per-model context length, USD/1M pricing, and <c>reasoning</c> effort support.
    /// Prepends <c>openrouter/auto</c> when at least one model is discovered and auto is absent.
    /// </summary>
    public static IReadOnlyList<DiscoveredOpenRouterModel> ParseOpenRouterModels(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (!TryGetPropertyCaseInsensitive(doc.RootElement, "data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<DiscoveredOpenRouterModel>();
            }

            var result = new List<DiscoveredOpenRouterModel>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasAuto = false;

            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                if (!TryGetPropertyCaseInsensitive(item, "id", out var idProp) ||
                    idProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var modelId = idProp.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(modelId)) continue;

                // Batch-only endpoints (e.g. deepseek/...:batch) cannot serve /chat/completions
                // and fail with HTTP 404. Exclude them from interactive model discovery.
                if (modelId.EndsWith(":batch", StringComparison.OrdinalIgnoreCase)) continue;

                var isAuto = string.Equals(modelId, "openrouter/auto", StringComparison.OrdinalIgnoreCase);
                if (!isAuto && !IsTextOutputOpenRouterModel(item)) continue;

                var supportedParams = ReadSupportedParameters(item);
                if (!isAuto && !supportedParams.Contains("tools")) continue;

                if (!seen.Add(modelId)) continue;
                if (isAuto) hasAuto = true;

                var displayName = modelId;
                if (TryGetPropertyCaseInsensitive(item, "name", out var nameProp) &&
                    nameProp.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(nameProp.GetString()))
                {
                    displayName = nameProp.GetString()!.Trim();
                }

                var maxContext = ExtractModelContextLimit(item);
                if (maxContext is > 0)
                {
                    ModelContextLimits.RecordLearnedLimit(modelId, maxContext.Value);
                }

                var efforts = supportedParams.Contains("reasoning")
                    ? DriverEffortContracts.OpenRouter
                    : Array.Empty<string>();

                var (promptPrice, completionPrice) = ExtractOpenRouterPricing(item);

                result.Add(new DiscoveredOpenRouterModel(
                    Id: modelId,
                    Name: displayName,
                    MaxContextTokens: maxContext,
                    Efforts: efforts,
                    PromptPricePerMillion: promptPrice,
                    CompletionPricePerMillion: completionPrice));
            }

            if (!hasAuto && result.Count > 0)
            {
                result.Insert(0, new DiscoveredOpenRouterModel(
                    Id: "openrouter/auto",
                    Name: "Auto Router (OpenRouter)",
                    MaxContextTokens: null,
                    Efforts: Array.Empty<string>(),
                    PromptPricePerMillion: null,
                    CompletionPricePerMillion: null));
            }

            return result;
        }
        catch (JsonException)
        {
            return Array.Empty<DiscoveredOpenRouterModel>();
        }
    }

    private static bool IsTextOutputOpenRouterModel(JsonElement item)
    {
        if (!TryGetPropertyCaseInsensitive(item, "architecture", out var arch) ||
            arch.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        if (TryGetPropertyCaseInsensitive(arch, "output_modalities", out var outputs) &&
            outputs.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in outputs.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String &&
                    string.Equals(entry.GetString()?.Trim(), "text", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        if (TryGetPropertyCaseInsensitive(arch, "modality", out var modality) &&
            modality.ValueKind == JsonValueKind.String)
        {
            var raw = modality.GetString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var arrowIdx = raw.IndexOf("->", StringComparison.Ordinal);
                var outputSide = arrowIdx >= 0 ? raw[(arrowIdx + 2)..] : raw;
                return outputSide.Contains("text", StringComparison.OrdinalIgnoreCase);
            }
        }

        return true;
    }

    private static HashSet<string> ReadSupportedParameters(JsonElement item)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!TryGetPropertyCaseInsensitive(item, "supported_parameters", out var paramsProp) ||
            paramsProp.ValueKind != JsonValueKind.Array)
        {
            return set;
        }

        foreach (var entry in paramsProp.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                var s = entry.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(s)) set.Add(s);
            }
        }

        return set;
    }

    private static (double? PromptPerMillion, double? CompletionPerMillion) ExtractOpenRouterPricing(JsonElement item)
    {
        if (!TryGetPropertyCaseInsensitive(item, "pricing", out var pricing) ||
            pricing.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        return (
            TryExtractPricePerMillion(pricing, "prompt"),
            TryExtractPricePerMillion(pricing, "completion"));
    }

    private static double? TryExtractPricePerMillion(JsonElement pricing, string field)
    {
        if (!TryGetPropertyCaseInsensitive(pricing, field, out var prop)) return null;

        double perToken;
        if (prop.ValueKind == JsonValueKind.Number)
        {
            if (!prop.TryGetDouble(out perToken)) return null;
        }
        else if (prop.ValueKind == JsonValueKind.String)
        {
            var raw = prop.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(raw) ||
                !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out perToken))
            {
                return null;
            }
        }
        else
        {
            return null;
        }

        // OpenRouter encodes dynamic/variable pricing as negative values (e.g. "-1").
        if (double.IsNaN(perToken) || double.IsInfinity(perToken) || perToken < 0)
        {
            return null;
        }

        return Math.Round(perToken * 1_000_000.0, 4);
    }

    private static readonly string[] ContextLimitFieldNames =
    {
        "max_context_window_tokens",
        "max_prompt_tokens",
        "max_model_len",
        "context_length",
        "max_context_length",
        "context_window",
        "max_input_tokens",
        "input_token_limit",
        "n_ctx"
    };

    private static readonly string[] NestedContextContainerNames =
    {
        "capabilities",
        "limits",
        "top_provider",
        "architecture",
        "model_info"
    };

    private static int? ExtractModelContextLimit(JsonElement item)
    {
        foreach (var field in ContextLimitFieldNames)
        {
            if (TryGetPositiveInt(item, field, out var direct))
            {
                return direct;
            }
        }

        foreach (var containerName in NestedContextContainerNames)
        {
            if (TryGetPropertyCaseInsensitive(item, containerName, out var nested) &&
                nested.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in ContextLimitFieldNames)
                {
                    if (TryGetPositiveInt(nested, field, out var nestedVal))
                    {
                        return nestedVal;
                    }
                }

                foreach (var subContainer in NestedContextContainerNames)
                {
                    if (TryGetPropertyCaseInsensitive(nested, subContainer, out var subNested) &&
                        subNested.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var field in ContextLimitFieldNames)
                        {
                            if (TryGetPositiveInt(subNested, field, out var subVal))
                            {
                                return subVal;
                            }
                        }
                    }
                }
            }
        }

        return null;
    }

    private static bool TryGetPositiveInt(JsonElement obj, string name, out int result)
    {
        result = 0;
        if (!TryGetPropertyCaseInsensitive(obj, name, out var prop)) return false;

        if (prop.ValueKind == JsonValueKind.Number)
        {
            if (prop.TryGetInt32(out var val) && val > 0)
            {
                result = val;
                return true;
            }
            if (prop.TryGetInt64(out var lVal) && lVal > 0 && lVal <= int.MaxValue)
            {
                result = (int)lVal;
                return true;
            }
        }
        else if (prop.ValueKind == JsonValueKind.String &&
                 int.TryParse(prop.GetString(), out var parsed) && parsed > 0)
        {
            result = parsed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Collapses the ways one server can be written down into a single comparable form.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shared by the probe's cache key and by a declaration's endpoint binding, and it has to be
    /// shared: two code paths describe the same server - an operator types a URL into a field, a
    /// runner reports the one it actually dialled - and if the two comparisons disagreed about what
    /// counts as "the same server", one of them would be wrong in a way nobody could reproduce.
    /// </para>
    /// <para>
    /// Tolerates the differences that are not configuration differences: surrounding whitespace, a
    /// trailing slash, an operator who pasted the full <c>.../models</c> path, and host casing,
    /// which URLs treat as case-insensitive.
    /// </para>
    /// </remarks>
    /// <returns>The normalized form, or null when there is no endpoint to normalize.</returns>
    public static string? NormalizeEndpoint(string? endpoint)
    {
        var trimmed = endpoint?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return null;

        var normalized = trimmed.TrimEnd('/');
        if (normalized.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^"/models".Length].TrimEnd('/');
        }

        return normalized.ToLowerInvariant();
    }

    /// <summary>
    /// Turns a configured endpoint into the models URL, tolerating an operator who pasted the
    /// full <c>.../models</c> path.
    /// </summary>
    public static string ToModelsUrl(string endpoint)
    {
        var trimmed = endpoint.Trim().TrimEnd('/');
        return trimmed.EndsWith("/models", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed}/models";
    }

    /// <summary>
    /// Case-insensitive property lookup. OpenAI-compatible servers are third-party software and
    /// some of them serialize <c>Data</c>/<c>ID</c>.
    /// </summary>
    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out value)) return true;

            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
