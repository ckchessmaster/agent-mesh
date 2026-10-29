using System.Collections.Concurrent;
using System.Text.Json;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Services;

namespace AgentMesh.Api.Services;

/// <summary>
/// Where a capability value came from. The dashboard must render these differently: a probed
/// model list is the account's real catalogue, a static preset is a bundled guess, and
/// "unavailable" is the honest answer when a provider could not be read.
/// </summary>
public enum CapabilitySourceKind
{
    /// <summary>Read from the driver itself (CLI probe or HTTP model listing).</summary>
    Probed,

    /// <summary>
    /// Bundled with the product because the driver exposes no discovery surface. Plausible, not
    /// verified against this install.
    /// </summary>
    StaticPreset,

    /// <summary>Supplied by an operator in the driver's capabilities blob.</summary>
    UserOverride,

    /// <summary>
    /// The driver exists but could not be read (not installed, signed out, unreachable). The UI
    /// says so instead of showing a list the user will trust and then send back to the driver.
    /// </summary>
    Unavailable
}

/// <summary>A model the driver can run, with the options that model actually accepts.</summary>
/// <param name="Efforts">
/// Effort values to offer. Only values the adapter will forward verbatim belong here - see
/// <see cref="DriverCapabilityReading.EffortSource"/>.
/// </param>
/// <param name="ContextSizes">
/// Context options for this model, or null when the driver cannot be told the window size.
/// </param>
public sealed record DriverModelCapability(
    string Id,
    string Name,
    IReadOnlyList<string> Efforts,
    IReadOnlyList<string>? ContextSizes = null);

/// <summary>
/// A capability reading for one driver. Every field is either probed, preset, operator-supplied,
/// or honestly absent; <see cref="ModelSource"/> and <see cref="EffortSource"/> say which.
/// </summary>
public sealed record DriverCapabilityReading(
    string Adapter,
    IReadOnlyList<DriverModelCapability> Models,
    CapabilitySourceKind ModelSource,
    DateTimeOffset ObservedAt,
    /// <summary>
    /// False when the driver has no way to be told its context window, so the UI hides the picker
    /// instead of offering a control that changes nothing. <c>agy</c> is the case that motivated
    /// this field: the selector offered 128k-2M and <c>AgyAdapter</c> never forwarded any of it.
    /// </summary>
    bool SupportsContextSelection = true,
    string? EffortSource = null,
    /// <summary>Populated when <see cref="ModelSource"/> is unavailable, so the UI can explain.</summary>
    string? UnavailableReason = null)
{
    /// <summary>
    /// True when the driver itself could not be read, regardless of whether an operator-supplied
    /// list is being shown in its place.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>ModelSource == Unavailable</c>: once an operator's hand-written models
    /// are merged in, the source becomes <c>user_override</c> while the driver is still unreadable.
    /// The probe uses this to pick the short negative TTL, so a host that gets signed in between
    /// page loads is re-read promptly instead of staying empty for the full positive TTL.
    /// </remarks>
    public bool IsUnavailable => ModelSource == CapabilitySourceKind.Unavailable || UnavailableReason != null;
}

/// <summary>
/// Identifies the driver whose capabilities are being resolved.
/// </summary>
/// <param name="Endpoint">
/// The OpenAI-compatible base URL when the driver speaks HTTP. Doubles as part of the cache key,
/// because two drivers pointing at different servers have genuinely different catalogues.
/// </param>
/// <param name="OperatorModels">
/// The <c>models</c> array from the driver's capabilities blob, if an operator wrote one.
/// </param>
/// <param name="ForceRefresh">
/// Bypass the TTL cache. Only honoured for authenticated callers - see
/// <c>SystemController.GetCapabilities</c>.
/// </param>
public sealed record DriverCapabilityProbeRequest(
    string DriverId,
    RuntimeType RuntimeType,
    string? Adapter,
    string? Endpoint,
    IReadOnlyList<string>? OperatorModels = null,
    string? DefaultModel = null,
    bool ForceRefresh = false);

/// <summary>
/// Reads a driver's real capabilities (models, efforts, context windows) from the driver itself.
/// </summary>
/// <remarks>
/// Same contract as <see cref="IDriverQuotaProbe"/>: return <c>null</c> when the driver cannot be
/// read, and never substitute a plausible default. A fabricated model list is worse than an empty
/// one because the user picks from it and the pick is then sent to the driver.
/// </remarks>
public interface IDriverCapabilityProbe
{
    Task<DriverCapabilityReading?> ProbeAsync(
        DriverCapabilityProbeRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// No-op probe for hosts that do not want the API shelling out, and for tests.
/// </summary>
public sealed class NullDriverCapabilityProbe : IDriverCapabilityProbe
{
    public static readonly NullDriverCapabilityProbe Instance = new();

    public Task<DriverCapabilityReading?> ProbeAsync(
        DriverCapabilityProbeRequest request,
        CancellationToken ct = default)
        => Task.FromResult<DriverCapabilityReading?>(null);
}

/// <summary>
/// Per-driver capability resolution with single-flight and positive/negative TTL caching,
/// modelled directly on <see cref="AgyDriverQuotaProbe"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam that lets the API stop guessing. <c>SystemController</c> used to hold one
/// hardcoded table per *adapter*, so two OpenAI-compatible drivers aimed at different servers were
/// handed the same six invented models, and a signed-out <c>agy</c> host got a five-model
/// catalogue it had never read.
/// </para>
/// <para>
/// When the runner-pushed capability manifest design lands (option C in
/// <c>docs/driver-capability-discovery.md</c>), the runner fills this same interface from the
/// registration payload and <c>SystemController</c> does not change.
/// </para>
/// </remarks>
public sealed class DriverCapabilityProbe : IDriverCapabilityProbe
{
    /// <summary>The <c>agy</c> subcommand that lists models.</summary>
    internal const string AgyModelArguments = "models";

    internal static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan DefaultNegativeTtl = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Upper bound on concurrent live probes. The endpoint is <c>[AllowAnonymous]</c>, so without
    /// a bound a caller could queue subprocesses and HTTP calls faster than they complete. A
    /// saturated gate makes the request fall back to the cached (possibly stale) reading instead
    /// of spawning more work.
    /// </summary>
    internal const int MaxConcurrentProbes = 2;

    /// <summary>How long a caller waits for a gate slot before giving up and serving stale.</summary>
    internal static readonly TimeSpan GateWait = TimeSpan.FromSeconds(2);

    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    {
        Timeout = ProbeTimeout
    };

    private readonly ConcurrentDictionary<string, (DateTimeOffset ObservedAt, DriverCapabilityReading? Reading)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<DriverCapabilityReading?>> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _gate;

    private readonly Func<TimeSpan> _ttlProvider;
    private readonly Func<TimeSpan> _negativeTtlProvider;
    private readonly Func<string?> _agyExecutableResolver;
    private readonly Func<string, string, TimeSpan, CancellationToken, Task<string?>> _cliRunner;
    private readonly Func<string, string?, CancellationToken, Task<IReadOnlyList<string>?>> _httpModelLister;

    public DriverCapabilityProbe()
        : this(() => DefaultTtl, () => DefaultNegativeTtl, AgyExecutableLocator.Resolve, DriverCliRunner.RunAsync)
    {
    }

    internal DriverCapabilityProbe(
        Func<TimeSpan> ttlProvider,
        Func<TimeSpan> negativeTtlProvider,
        Func<string?> agyExecutableResolver,
        Func<string, string, TimeSpan, CancellationToken, Task<string?>> cliRunner,
        Func<string, string?, CancellationToken, Task<IReadOnlyList<string>?>>? httpModelLister = null,
        int maxConcurrentProbes = MaxConcurrentProbes)
    {
        _ttlProvider = ttlProvider;
        _negativeTtlProvider = negativeTtlProvider;
        _agyExecutableResolver = agyExecutableResolver;
        _cliRunner = cliRunner;
        _httpModelLister = httpModelLister ?? ListOpenAiModelsAsync;
        _gate = new SemaphoreSlim(maxConcurrentProbes, maxConcurrentProbes);
    }

    public async Task<DriverCapabilityReading?> ProbeAsync(
        DriverCapabilityProbeRequest request,
        CancellationToken ct = default)
    {
        var adapter = NormalizeAdapter(request);
        if (adapter == null) return null; // generic / unknown: nothing to discover

        var cacheKey = BuildCacheKey(adapter, request);

        if (!request.ForceRefresh && _cache.TryGetValue(cacheKey, out var cached))
        {
            // "Unavailable" is an answer, but a short-lived one: it usually means "not signed in
            // yet". Caching it for the full positive TTL would leave the picker empty for minutes
            // after the operator fixes the very thing the message told them to fix.
            var allowedAge = cached.Reading is { IsUnavailable: false } ? _ttlProvider() : _negativeTtlProvider();
            if (DateTimeOffset.UtcNow - cached.ObservedAt < allowedAge)
            {
                return cached.Reading;
            }
        }

        // Single-flight per cache key: the dashboard fans this out over every driver on load, and
        // drivers that share an endpoint (or share one agy install) share one answer.
        var leaderSource =
            new TaskCompletionSource<DriverCapabilityReading?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = _inFlight.GetOrAdd(cacheKey, leaderSource);

        if (!ReferenceEquals(existing, leaderSource))
        {
            // WaitAsync (unlike WhenAny+Delay) does not propagate cancellation into the shared
            // flight, and leaves no timer registered once the flight wins.
            return ct.CanBeCanceled ? await existing.Task.WaitAsync(ct) : await existing.Task;
        }

        DriverCapabilityReading? reading = null;
        var haveSlot = false;
        try
        {
            // Bound the fan-out rather than queueing unbounded subprocesses: an anonymous caller
            // must not be able to turn `agentId` permutations into a process-spawning DoS.
            haveSlot = await _gate.WaitAsync(GateWait, CancellationToken.None);

            reading = !haveSlot
                ? StaleOrNothing(cacheKey)
                : adapter switch
                {
                    DriverAdapters.Agy => await ProbeAgyAsync(request, ct),
                    DriverAdapters.OpenAi => await ProbeOpenAiAsync(request, ct),
                    _ => PresetOnly(adapter, request)
                };
        }
        catch (Exception ex)
        {
            // A bug in the probe is a fact worth reporting, not something to swallow into null:
            // null means "this driver type has no discovery surface", which is a different and
            // wrong explanation for "resolution crashed". The flight must still complete, because
            // a faulted leader task would be handed to every later caller forever.
            reading = Unavailable(adapter, $"Capability resolution failed: {ex.Message}");
        }
        finally
        {
            // Negative results are cached too, so a signed-out host is not re-shelled on every
            // dashboard poll.
            _cache[cacheKey] = (DateTimeOffset.UtcNow, reading);

            // Order matters: publish the cache before releasing waiters and drop the in-flight
            // entry last, so a caller arriving mid-handoff either sees the fresh cache or joins a
            // live flight. A leaked entry would pin the key to a completed task and the picker
            // would stop ageing; a faulted task would poison every later caller.
            leaderSource.SetResult(reading);
            _inFlight.TryRemove(cacheKey, out _);

            if (haveSlot) _gate.Release();
        }

        return reading;
    }

    /// <summary>
    /// The adapter whose discovery surface to use, or null when the driver has none.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="AgyDriverQuotaProbe.IsAntigravity"/>: the recorded adapter is the real
    /// discriminator, because <see cref="RuntimeType.CliAgy"/> is the schema default for every CLI
    /// driver. An endpoint means the driver speaks OpenAI-compatible HTTP regardless of what its
    /// runtime enum says.
    /// </remarks>
    internal static string? NormalizeAdapter(DriverCapabilityProbeRequest request)
    {
        var adapter = DriverAdapters.Canonicalize(request.Adapter);

        // `generic` runs an arbitrary command with no discovery surface and no opinion to offer.
        if (adapter is null or DriverAdapters.Generic)
        {
            if (string.IsNullOrWhiteSpace(request.Endpoint)) return null;

            // No adapter recorded (rows written before the field existed): infer from transport.
            return DriverAdapters.OpenAi;
        }

        return adapter;
    }

    /// <summary>
    /// Cache key: adapter + normalized endpoint, so two OpenAI-compatible servers get two
    /// catalogues while N drivers on one server share one probe. For <c>agy</c> the reading is
    /// host-level (it is the account signed in on this machine), so every agy driver shares one
    /// key - exactly like the quota probe.
    /// </summary>
    internal static string BuildCacheKey(string adapter, DriverCapabilityProbeRequest request)
    {
        if (adapter == DriverAdapters.Agy) return DriverAdapters.Agy;

        var endpoint = request.Endpoint?.Trim();
        if (string.IsNullOrWhiteSpace(endpoint)) return $"{adapter}:default";

        var normalized = endpoint.TrimEnd('/');
        if (normalized.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^"/models".Length].TrimEnd('/');
        }

        return $"{adapter}:{normalized.ToLowerInvariant()}";
    }

    private async Task<DriverCapabilityReading?> ProbeAgyAsync(
        DriverCapabilityProbeRequest request,
        CancellationToken ct)
    {
        var executable = _agyExecutableResolver();
        if (string.IsNullOrWhiteSpace(executable))
        {
            return WithOperatorModels(Unavailable(DriverAdapters.Agy,
                "The agy CLI is not installed on the API host, so its model list cannot be read."), request);
        }

        // Deliberately not the caller's token: the flight is shared between drivers, so one
        // browser disconnecting must not cancel everyone else's read. DriverCliRunner enforces its
        // own hard timeout instead.
        var raw = await _cliRunner(executable, AgyModelArguments, ProbeTimeout, CancellationToken.None);
        if (string.IsNullOrWhiteSpace(raw))
        {
            // `agy models` on a signed-out host prints "Error: Please sign in..." and exits 1,
            // which the runner collapses to null. That is an answer, not a crash: report it as
            // unavailable instead of falling back to a fabricated catalogue.
            return WithOperatorModels(Unavailable(DriverAdapters.Agy,
                "The agy CLI returned no model list; it is probably not signed in on the API host."), request);
        }

        var models = ParseAgyModels(raw);
        if (models.Count == 0)
        {
            // The CLI answered, but not in a shape we could read. An empty probed list would be
            // indistinguishable from "this account has no models", and a preset would be a guess.
            // Both are worse than naming the parse failure.
            return WithOperatorModels(Unavailable(DriverAdapters.Agy,
                "The agy CLI printed no model rows we could parse; its output format may have changed."), request);
        }

        return WithOperatorModels(
            new DriverCapabilityReading(
                Adapter: DriverAdapters.Agy,
                Models: models,
                ModelSource: CapabilitySourceKind.Probed,
                ObservedAt: DateTimeOffset.UtcNow,
                // `agy --help` exposes --model, --effort, --mode and --agent, and no context-size
                // flag. AgyAdapter forwards none either, so the picker was a no-op on this driver.
                SupportsContextSelection: false,
                // Derived by stripping -low/-medium/-high/-xhigh/-max from ids the CLI itself
                // printed, then filtered against the effort set `agy --help` documents.
                EffortSource: "derived:agy-model-suffixes"),
            request);
    }

    /// <summary>
    /// Parses <c>agy models</c> output. The CLI prints one <c>&lt;id&gt; &lt;display name&gt;</c>
    /// row per effort variant and offers no JSON mode (<c>--output-format</c> is a print-mode
    /// flag), so text parsing is the only channel available.
    /// </summary>
    /// <remarks>
    /// Effort variants are grouped back into one entry per base model: the picker selects a model
    /// and an effort separately, and <c>AgyAdapter</c> passes <c>--model</c> without the suffix.
    /// </remarks>
    internal static IReadOnlyList<DriverModelCapability> ParseAgyModels(string raw)
    {
        var grouped = new Dictionary<string, (string DisplayName, HashSet<string> Efforts)>(
            StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            // Spinners, progress lines and the "Fetching available models..." banner.
            if (IsBrailleSpinner(trimmed[0])) continue;
            if (trimmed.StartsWith("fetching ", StringComparison.OrdinalIgnoreCase)) continue;
            if (trimmed.StartsWith("error", StringComparison.OrdinalIgnoreCase)) continue;
            if (trimmed.StartsWith("available models", StringComparison.OrdinalIgnoreCase)) continue;
            if (trimmed.StartsWith("model", StringComparison.OrdinalIgnoreCase) &&
                (trimmed.Contains("name", StringComparison.OrdinalIgnoreCase) ||
                 trimmed.Contains("id", StringComparison.OrdinalIgnoreCase)))
            {
                continue; // header row
            }

            var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
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
    internal static bool LooksLikeModelId(string token)
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

    internal static string? ExtractEffortSuffix(string id)
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
    internal static bool AgyAdapterAcceptsEffort(string effort) =>
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

    private async Task<DriverCapabilityReading?> ProbeOpenAiAsync(
        DriverCapabilityProbeRequest request,
        CancellationToken ct)
    {
        var endpoint = request.Endpoint?.Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            // No URL means we would be probing a server we cannot name. The preset list used to
            // stand in here and looked exactly like a real answer.
            return WithOperatorModels(
                Unavailable(DriverAdapters.OpenAi, "No endpoint is configured for this driver."), request);
        }

        var ids = await _httpModelLister(endpoint, ApiKeyValue(request), ct);
        if (ids == null)
        {
            return WithOperatorModels(Unavailable(DriverAdapters.OpenAi,
                $"The server at {endpoint.TrimEnd('/')} did not return a model list."), request);
        }

        if (ids.Count == 0)
        {
            return WithOperatorModels(Unavailable(DriverAdapters.OpenAi,
                $"The server at {endpoint.TrimEnd('/')} reported no models."), request);
        }

        var models = ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => id.Trim())
            // The listing carries no capacity information, so per-model sizes come from the shared
            // table in AgentMesh.Core - the same table OpenAiAdapter applies at runtime, which is
            // what stops the picker and the run from disagreeing.
            .Select(id => new DriverModelCapability(
                id,
                id,
                Array.Empty<string>(),
                ContextSizesForModel(id)))
            .ToList();

        return WithOperatorModels(
            new DriverCapabilityReading(
                Adapter: DriverAdapters.OpenAi,
                Models: models,
                ModelSource: CapabilitySourceKind.Probed,
                ObservedAt: DateTimeOffset.UtcNow,
                SupportsContextSelection: true,
                // An OpenAI /models response has no effort surface at all.
                EffortSource: null),
            request);
    }

    /// <summary>
    /// Context options for a listed model, derived from the shared resolution table rather than a
    /// second table in the controller.
    /// </summary>
    /// <remarks>
    /// Only sizes at or below the model's resolved window are offered: a picker that advertises
    /// 256k for a 128k model invites the user to select a value the runtime then clamps.
    /// </remarks>
    internal static IReadOnlyList<string> ContextSizesForModel(string modelId)
    {
        var resolved = ModelContextLimits.Resolve(modelId, contextSizeOption: null);
        var sizes = new List<string> { "default" };

        foreach (var candidate in DriverCapabilityPresets.GenericContextSizes.Skip(1))
        {
            var tokens = ModelContextLimits.ParseOption(candidate);
            if (tokens != null && tokens.Value <= resolved.Tokens) sizes.Add(candidate);
        }

        return sizes;
    }

    /// <summary>
    /// The API key for a live model listing, read from the environment rather than the stored
    /// blob.
    /// </summary>
    /// <remarks>
    /// <c>DriverCapabilitiesView</c> deliberately redacts the key (it must not reach the browser
    /// bundle), and re-reading the plaintext blob here would put a secret on an
    /// <c>[AllowAnonymous]</c> request path. Operators that need an authenticated listing set
    /// <c>AGENTMESH_OPENAI_API_KEY</c> on the API host; drivers that do not have one get an honest
    /// "unavailable" instead of a preset dressed up as a probe.
    /// </remarks>
    private static string? ApiKeyValue(DriverCapabilityProbeRequest request)
    {
        _ = request; // no per-driver secrets are read on this path
        var configured = Environment.GetEnvironmentVariable("AGENTMESH_OPENAI_API_KEY");
        return string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
    }

    private static async Task<IReadOnlyList<string>?> ListOpenAiModelsAsync(
        string endpoint,
        string? apiKey,
        CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ProbeTimeout);

            using var httpReq = new HttpRequestMessage(HttpMethod.Get, OpenAiModelListing.ToModelsUrl(endpoint));
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                httpReq.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var res = await SharedHttp.SendAsync(httpReq, timeoutCts.Token);
            if (!res.IsSuccessStatusCode) return null;

            var json = await res.Content.ReadAsStringAsync(timeoutCts.Token);
            return OpenAiModelListing.ParseIds(json);
        }
        catch (Exception)
        {
            // Unreachable, TLS failure, non-JSON body: all mean "we could not read this server".
            return null;
        }
    }

    private static DriverCapabilityReading? PresetOnly(string adapter, DriverCapabilityProbeRequest request)
    {
        var preset = DriverCapabilityPresets.For(adapter, DateTimeOffset.UtcNow);
        if (preset == null)
        {
            // Unknown adapter: no bundled opinion. An operator who hand-wrote a model list still
            // gets it back, because that part is not a guess about a driver we failed to read.
            return request.OperatorModels?.Count > 0 || !string.IsNullOrWhiteSpace(request.DefaultModel)
                ? WithOperatorModels(
                    Unavailable(adapter, "This driver type has no capability discovery surface."), request)
                : null;
        }

        return WithOperatorModels(preset, request);
    }

    /// <summary>
    /// Layers the operator's <c>models</c> array and <c>defaultModel</c> over whatever the probe
    /// produced, marking the result as an override so the UI can say which list is whose.
    /// </summary>
    private static DriverCapabilityReading WithOperatorModels(
        DriverCapabilityReading reading,
        DriverCapabilityProbeRequest request)
    {
        var overrides = (request.OperatorModels ?? Array.Empty<string>())
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(request.DefaultModel))
        {
            var model = request.DefaultModel.Trim();
            if (!overrides.Contains(model, StringComparer.OrdinalIgnoreCase)) overrides.Insert(0, model);
        }

        if (overrides.Count == 0) return reading;

        var merged = new List<DriverModelCapability>(overrides.Count + reading.Models.Count);
        foreach (var id in overrides)
        {
            var existing = reading.Models.FirstOrDefault(
                m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

            merged.Add(existing ?? new DriverModelCapability(id, id, Array.Empty<string>(), null));
        }

        foreach (var model in reading.Models)
        {
            if (!overrides.Contains(model.Id, StringComparer.OrdinalIgnoreCase)) merged.Add(model);
        }

        // Once an operator entry is in the list, the list as a whole is no longer "what the driver
        // said": it is a hand-written list that happens to include probed or preset entries. The
        // UI labels it as an override and still shows the reason the driver could not be read.
        return reading with
        {
            Models = merged,
            ModelSource = CapabilitySourceKind.UserOverride
        };
    }

    private static DriverCapabilityReading Unavailable(string adapter, string reason) =>
        new(
            Adapter: adapter,
            Models: Array.Empty<DriverModelCapability>(),
            ModelSource: CapabilitySourceKind.Unavailable,
            ObservedAt: DateTimeOffset.UtcNow,
            SupportsContextSelection: ModelContextLimits.SupportsContextSelection(adapter),
            UnavailableReason: reason);

    /// <summary>
    /// What we can serve when the live-probe gate is saturated: the last known reading, even if
    /// stale, rather than spawning another process.
    /// </summary>
    /// <remarks>
    /// When nothing has ever been read there is still an answer to give, and it is not null: null
    /// would be rendered as "this driver type has no discovery surface", which is a different and
    /// wrong explanation for "we are already probing it".
    /// </remarks>
    private DriverCapabilityReading? StaleOrNothing(string cacheKey)
    {
        if (_cache.TryGetValue(cacheKey, out var entry) && entry.Reading != null) return entry.Reading;

        var adapter = cacheKey.Split(':', 2)[0];

        return new DriverCapabilityReading(
            Adapter: adapter,
            Models: Array.Empty<DriverModelCapability>(),
            ModelSource: CapabilitySourceKind.Unavailable,
            ObservedAt: DateTimeOffset.UtcNow,
            SupportsContextSelection: ModelContextLimits.SupportsContextSelection(adapter),
            UnavailableReason: "A capability probe for this driver is already running; try again shortly.");
    }
}
