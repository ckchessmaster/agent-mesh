using System.Collections.Concurrent;
using System.Text.Json;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
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
    /// Reported by the runner that owns the driver, pushed at registration (option C). Trusted
    /// more than a probe's guess about a server it cannot see, but less than a direct read, and
    /// labelled as self-reported for that reason: it is a claim from the machine next to the
    /// binary, not a verification by us.
    /// </summary>
    Declared,

    /// <summary>
    /// The driver exists but could not be read (not installed, signed out, unreachable). The UI
    /// says so instead of showing a list the user will trust and then send back to the driver.
    /// </summary>
    Unavailable
}

/// <summary>
/// The literal the dashboard switches on for a source kind.
/// </summary>
/// <remarks>
/// <para>
/// Written out rather than produced with <c>ToString().ToLowerInvariant()</c>, which was the
/// original implementation and was wrong for every multi-word member: <c>StaticPreset</c> became
/// <c>"staticpreset"</c> and <c>UserOverride</c> became <c>"useroverride"</c>. Neither string is in
/// the UI's <c>CapabilitySource</c> union, so <c>normalizeSource</c> fell through to
/// <c>'unavailable'</c>. The picker itself kept working - the dashboard branches on list length, not
/// on source - but the caption under it read "Model list unavailable", contradicting the populated
/// select above it. Silent, and confined to the two sources that are not the happy path, which is
/// why pinning a single wire value did not catch it.
/// </para>
/// <para>
/// The switch is exhaustive with no discard arm on purpose. Adding a member to
/// <see cref="CapabilitySourceKind"/> then produces compiler warning CS8524 here rather than
/// silently degrading to a wrong label at runtime. That is a warning, not an error - this repo does
/// not set TreatWarningsAsErrors - so the hard guard is <c>CapabilitiesWireFormatTests</c>, which
/// enumerates every member and asserts its wire value is present in the TypeScript union.
/// </para>
/// </remarks>
public static class CapabilitySourceKindExtensions
{
    public static string ToWireValue(this CapabilitySourceKind source) => source switch
    {
        CapabilitySourceKind.Probed => "probed",
        CapabilitySourceKind.Declared => "declared",
        CapabilitySourceKind.StaticPreset => "static_preset",
        CapabilitySourceKind.UserOverride => "user_override",
        CapabilitySourceKind.Unavailable => "unavailable"
    };
}



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
/// <param name="Declared">
/// What the runner pushed about its own driver (option C), read out of the driver's capabilities
/// blob. Preferred over a live probe while fresh, because the runner is the only component that
/// can read a binary installed on its machine rather than on the API host.
/// </param>
/// <param name="ForceRefresh">
/// Bypass the TTL cache. Only honoured for authenticated callers - see
/// <c>SystemController.GetCapabilities</c>. A fresh declaration is still preferred: refresh means
/// "go look at the provider again", not "ignore what the driver told us about itself".
/// </param>
public sealed record DriverCapabilityProbeRequest(
    string DriverId,
    RuntimeType RuntimeType,
    string? Adapter,
    string? Endpoint,
    IReadOnlyList<string>? OperatorModels = null,
    string? DefaultModel = null,
    bool ForceRefresh = false,
    DriverCapabilityDeclaration? Declared = null,
    string? ApiKey = null,
    DriverSidecarConfig? Sidecar = null,
    IReadOnlyDictionary<string, int>? LearnedContextLimits = null,
    string? OAuthToken = null);

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

    void InvalidateCache() { }
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
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

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
    private readonly bool _hasCustomHttpLister;
    private readonly IPersonaContainerManager? _containerManager;

    public DriverCapabilityProbe(IPersonaContainerManager? containerManager = null)
        : this(() => DefaultTtl, () => DefaultNegativeTtl, AgyExecutableLocator.Resolve, DriverCliRunner.RunAsync)
    {
        _containerManager = containerManager;
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
        _hasCustomHttpLister = httpModelLister != null;
        _httpModelLister = httpModelLister ?? ListOpenAiModelsAsync;
        _gate = new SemaphoreSlim(maxConcurrentProbes, maxConcurrentProbes);
    }

    public void InvalidateCache() => _cache.Clear();

    public async Task<DriverCapabilityReading?> ProbeAsync(
        DriverCapabilityProbeRequest request,
        CancellationToken ct = default)
    {
        // Option C: a fresh self-report from the runner wins outright.
        //
        // Two reasons this is checked before the adapter resolution and before the cache:
        //
        //   1. It is per-driver, while the cache below is keyed per adapter+endpoint because N
        //      drivers sharing one agy install share one answer. Caching a declaration there would
        //      hand one runner's catalogue to every other driver on that adapter.
        //   2. It removes the request-path I/O entirely - no subprocess, no outbound call - which
        //      is the point of the change. A driver whose runner is healthy never touches the
        //      probe machinery at all.
        //
        // A stale or absent declaration falls through to the live probe, which is what keeps
        // runners older than this change working exactly as they do today.
        if (TryDeclaredReading(request, out var declaredReading))
        {
            return WithOperatorModels(declaredReading, request);
        }

        var fallback = await ProbeCoreAsync(request, ct);

        // When the live probe on the API host could not read the driver (e.g. runner container is
        // asleep or the API host has no direct route), preserve a previously verified declaration
        // whose adapter and endpoint still match the row rather than blanking the picker.
        if ((fallback == null || fallback.Models.Count == 0) &&
            TryLastKnownDeclaration(request, out var lastKnownReading))
        {
            return WithOperatorModels(lastKnownReading, request);
        }

        // Applied here rather than inside ProbeCoreAsync so it covers every way the core can come
        // back empty: a fresh probe, a cached negative, and a caller that joined someone else's
        // in-flight read. Doing it only on the leader path meant the reason a driver showed for
        // having no models depended on which request happened to be first.
        return WithDeclaredReason(fallback, request);
    }

    /// <summary>
    /// The live resolution path: cache, single-flight, bounded gate, per-adapter discovery.
    /// </summary>
    private async Task<DriverCapabilityReading?> ProbeCoreAsync(
        DriverCapabilityProbeRequest request,
        CancellationToken ct)
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

        // Normalization is shared with a declaration's endpoint binding
        // (DriverModelDiscovery.NormalizeEndpoint) on purpose: both decide whether two spellings
        // name one server, and if they disagreed the cache would dedupe a pair the guard splits,
        // or vice versa. Behaviour is unchanged from the inline version this replaces.
        var normalized = DriverModelDiscovery.NormalizeEndpoint(request.Endpoint);
        if (normalized == null) return $"{adapter}:default";

        return $"{adapter}:{normalized}";
    }

    private async Task<DriverCapabilityReading?> ProbeAgyAsync(
        DriverCapabilityProbeRequest request,
        CancellationToken ct)
    {
        EnsureHostAgyTokenIfAvailable(request.OAuthToken, request.DriverId);

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

        var models = DriverModelDiscovery.ParseAgyModels(raw);
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

    private static void EnsureHostAgyTokenIfAvailable(string? storedToken, string? driverId)
    {
        try
        {
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userHome)) return;

            var hostTokenPath = Path.Combine(userHome, ".gemini", "antigravity-cli", "antigravity-oauth-token");
            if (File.Exists(hostTokenPath)) return;

            var candidateToken = storedToken;
            if (string.IsNullOrWhiteSpace(candidateToken))
            {
                var tokensDir = Path.Combine(userHome, ".agentmesh", "tokens");
                if (Directory.Exists(tokensDir))
                {
                    if (!string.IsNullOrWhiteSpace(driverId))
                    {
                        var driverOauth = Path.Combine(tokensDir, $"driver-{driverId.Trim().ToLowerInvariant()}.oauth");
                        if (File.Exists(driverOauth))
                        {
                            candidateToken = File.ReadAllText(driverOauth).Trim();
                        }
                    }

                    if (string.IsNullOrWhiteSpace(candidateToken))
                    {
                        var anyOauth = Directory.GetFiles(tokensDir, "*.oauth").FirstOrDefault();
                        if (anyOauth != null)
                        {
                            candidateToken = File.ReadAllText(anyOauth).Trim();
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(candidateToken))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(hostTokenPath)!);
                File.WriteAllText(hostTokenPath, candidateToken.Trim());
            }
        }
        catch
        {
            // Best-effort token restoration; never fail the probe on filesystem permissions.
        }
    }

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

        IReadOnlyList<DiscoveredOpenAiModel>? discovered;
        if (_hasCustomHttpLister)
        {
            var ids = await _httpModelLister(endpoint, ApiKeyValue(request), ct);
            discovered = ids?.Select(id => new DiscoveredOpenAiModel(id, null)).ToList();
        }
        else
        {
            discovered = await ListOpenAiModelsDetailedAsync(request, endpoint, ApiKeyValue(request), ct);
        }

        if (discovered == null)
        {
            return WithOperatorModels(Unavailable(DriverAdapters.OpenAi,
                $"The server at {endpoint.TrimEnd('/')} did not return a model list."), request);
        }

        if (discovered.Count == 0)
        {
            return WithOperatorModels(Unavailable(DriverAdapters.OpenAi,
                $"The server at {endpoint.TrimEnd('/')} reported no models."), request);
        }

        var models = discovered
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .GroupBy(m => m.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var id = g.Key;
                var maxTokens = g.FirstOrDefault(x => x.MaxContextTokens is > 0)?.MaxContextTokens;
                return new DriverModelCapability(
                    id,
                    id,
                    Array.Empty<string>(),
                    ContextSizesForModel(id, maxTokens, request.LearnedContextLimits));
            })
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
    /// Context options for a listed model, derived from the shared resolution table, an explicit
    /// provider-reported limit, or a learned overflow limit.
    /// </summary>
    /// <remarks>
    /// Only sizes at or below the model's verified/learned window are offered. When a model has no
    /// known, provider-reported, or learned limit yet, only <c>"default"</c> is returned (which
    /// runs at 1M and auto-detects the true limit on overflow).
    /// </remarks>
    internal static IReadOnlyList<string> ContextSizesForModel(
        string modelId,
        int? explicitMaxTokens = null,
        IReadOnlyDictionary<string, int>? learnedLimits = null)
    {
        int? ceiling = null;
        if (explicitMaxTokens is > 0)
        {
            ceiling = explicitMaxTokens.Value;
        }
        else if (learnedLimits != null &&
                 learnedLimits.TryGetValue(modelId, out var learned) &&
                 learned > 0)
        {
            ceiling = learned;
        }
        else
        {
            var resolved = ModelContextLimits.Resolve(modelId, contextSizeOption: null);
            if (!resolved.IsFallback)
            {
                ceiling = resolved.Tokens;
            }
        }

        var sizes = new List<string> { "default" };
        if (ceiling is null or <= 0)
        {
            return sizes;
        }

        var capped = Math.Min(ceiling.Value, ModelContextLimits.MaxSelectableTokenLimit);
        foreach (var candidate in DriverCapabilityPresets.GenericContextSizes.Skip(1))
        {
            var tokens = ModelContextLimits.ParseOption(candidate);
            if (tokens != null && tokens.Value <= capped) sizes.Add(candidate);
        }

        return sizes;
    }

    /// <summary>
    /// The API key for a live model listing, preferring the driver's stored key and falling back
    /// to <c>AGENTMESH_OPENAI_API_KEY</c> on the API host.
    /// </summary>
    private static string? ApiKeyValue(DriverCapabilityProbeRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            return request.ApiKey.Trim();
        }

        var configured = Environment.GetEnvironmentVariable("AGENTMESH_OPENAI_API_KEY");
        return string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
    }

    private async Task<IReadOnlyList<DiscoveredOpenAiModel>?> ListOpenAiModelsDetailedAsync(
        DriverCapabilityProbeRequest request,
        string endpoint,
        string? apiKey,
        CancellationToken ct)
    {
        if (request.Sidecar is { Enabled: true } && _containerManager != null)
        {
            try
            {
                var sidecarRes = await _containerManager.TestDriverEndpointAsync(
                    endpoint, apiKey, request.Sidecar, ct);
                if (sidecarRes.Success)
                {
                    if (!string.IsNullOrWhiteSpace(sidecarRes.ResponseBody))
                    {
                        var parsed = DriverModelDiscovery.ParseOpenAiModels(sidecarRes.ResponseBody);
                        if (parsed.Count > 0) return parsed;
                    }
                    if (sidecarRes.AvailableModels != null)
                    {
                        return sidecarRes.AvailableModels
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .Select(id => new DiscoveredOpenAiModel(id.Trim(), null))
                            .ToList();
                    }
                }
            }
            catch
            {
                // Fall through to direct HTTP attempt
            }
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ProbeTimeout);

            using var httpReq = new HttpRequestMessage(HttpMethod.Get, DriverModelDiscovery.ToModelsUrl(endpoint));
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                httpReq.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var res = await SharedHttp.SendAsync(httpReq, timeoutCts.Token);
            if (!res.IsSuccessStatusCode) return null;

            var json = await res.Content.ReadAsStringAsync(timeoutCts.Token);
            return DriverModelDiscovery.ParseOpenAiModels(json);
        }
        catch (Exception)
        {
            // Unreachable, TLS failure, non-JSON body: all mean "we could not read this server".
            return null;
        }
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

            using var httpReq = new HttpRequestMessage(HttpMethod.Get, DriverModelDiscovery.ToModelsUrl(endpoint));
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                httpReq.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var res = await SharedHttp.SendAsync(httpReq, timeoutCts.Token);
            if (!res.IsSuccessStatusCode) return null;

            var json = await res.Content.ReadAsStringAsync(timeoutCts.Token);
            return DriverModelDiscovery.ParseOpenAiModelIds(json);
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
    /// Whether a runner's self-report may be believed for this driver row at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both consumers of a declaration - the catalogue path and the reason path - have to apply the
    /// same checks, which is why this is one predicate rather than two sets of <c>if</c>s. They had
    /// already drifted: <see cref="WithDeclaredReason"/> consulted a declaration without checking
    /// its age or its adapter, so a runner that died last week could keep a driver showing last
    /// week's explanation, and a row repointed from <c>agy</c> to <c>openai</c> could be told to go
    /// sign into a driver it no longer is. A guard duplicated in two places is a guard that gets
    /// updated in one.
    /// </para>
    /// <para>
    /// Three things void a declaration:
    /// </para>
    /// <list type="number">
    ///   <item>It is older than <see cref="DriverCapabilityDeclaration.FreshnessWindow"/>. A
    ///   catalogue from a runner that died last week must not pin the picker forever, and neither
    ///   must its parting explanation.</item>
    ///   <item>Its adapter disagrees with the row. The blob is written by a runner but the row can
    ///   be edited underneath it: repointing a driver at a different adapter in the dashboard
    ///   leaves the previous runner's declaration in place until that runner re-registers. Serving
    ///   it would hand an agy catalogue to an OpenAI-compatible driver, which is the exact
    ///   per-driver confusion this line of work set out to fix.</item>
    ///   <item>Its endpoint disagrees with the row, when it declares one at all. The adapter check
    ///   cannot catch a repoint between two OpenAI-compatible servers - the adapter still matches -
    ///   so without this the old server's catalogue, or its 401, keeps answering for a driver that
    ///   now points elsewhere, until that runner happens to re-register.</item>
    /// </list>
    /// <para>
    /// A declaration that declares no endpoint is <em>unbound</em>, not mismatched. That covers
    /// <c>agy</c>, whose read is host-level and has no URL to bind to, and every runner built before
    /// the field existed. Treating absence as a mismatch would empty every picker in a
    /// mixed-version fleet the moment the API is upgraded - the rollout tail this design exists to
    /// survive.
    /// </para>
    /// </remarks>
    internal static bool TryTrustedDeclaration(
        DriverCapabilityProbeRequest request,
        DateTimeOffset now,
        out DriverCapabilityDeclaration declared,
        out string expectedAdapter)
    {
        declared = null!;
        expectedAdapter = null!;

        var candidate = request.Declared;
        if (candidate == null) return false;
        if (!candidate.IsFresh(now)) return false;

        var adapter = NormalizeAdapter(request);
        if (adapter == null) return false;

        var declaredAdapter = DriverAdapters.Canonicalize(candidate.Adapter);
        if (!string.Equals(declaredAdapter, adapter, StringComparison.OrdinalIgnoreCase)) return false;

        var declaredEndpoint = DriverModelDiscovery.NormalizeEndpoint(candidate.Endpoint);
        if (declaredEndpoint != null)
        {
            var rowEndpoint = DriverModelDiscovery.NormalizeEndpoint(request.Endpoint);
            if (!string.Equals(declaredEndpoint, rowEndpoint, StringComparison.OrdinalIgnoreCase)) return false;
        }

        declared = candidate;
        expectedAdapter = adapter;
        return true;
    }

    /// <summary>
    /// Builds a reading from the runner's own report of its driver, when there is one to trust.
    /// </summary>
    /// <remarks>
    /// Whether the declaration may be believed at all is decided by
    /// <see cref="TryTrustedDeclaration"/>, which the reason path shares so the two cannot drift
    /// apart again. The check specific to this method is the fourth: a declaration with no models is
    /// the runner saying "I could not read my own driver", and for an HTTP driver the API may still
    /// reach the server, so the probe gets its turn. The runner's explanation is recovered later by
    /// <see cref="WithDeclaredReason"/> if neither side produced a list.
    /// </remarks>
    internal static bool TryDeclaredReading(
        DriverCapabilityProbeRequest request,
        out DriverCapabilityReading reading,
        DateTimeOffset? now = null)
    {
        reading = null!;

        if (!TryTrustedDeclaration(request, now ?? DateTimeOffset.UtcNow, out var declared, out var expectedAdapter))
        {
            return false;
        }

        if (declared.Models.Count == 0) return false;

        reading = BuildReadingFromDeclaration(declared, expectedAdapter, request.LearnedContextLimits);
        return reading.Models.Count > 0;
    }

    /// <summary>
    /// Serves a previously persisted model catalogue when the live probe on the API host is
    /// unavailable (e.g. runner is offline or endpoint is only reachable via container sidecar),
    /// provided the declaration's adapter and endpoint still match the driver row.
    /// </summary>
    internal static bool TryLastKnownDeclaration(
        DriverCapabilityProbeRequest request,
        out DriverCapabilityReading reading)
    {
        reading = null!;
        var candidate = request.Declared;
        if (candidate == null || candidate.Models.Count == 0) return false;
        if (candidate.ObservedAt == default) return false;

        var adapter = NormalizeAdapter(request);
        if (adapter == null) return false;

        var declaredAdapter = DriverAdapters.Canonicalize(candidate.Adapter);
        if (!string.Equals(declaredAdapter, adapter, StringComparison.OrdinalIgnoreCase)) return false;

        var declaredEndpoint = DriverModelDiscovery.NormalizeEndpoint(candidate.Endpoint);
        if (declaredEndpoint != null)
        {
            var rowEndpoint = DriverModelDiscovery.NormalizeEndpoint(request.Endpoint);
            if (!string.Equals(declaredEndpoint, rowEndpoint, StringComparison.OrdinalIgnoreCase)) return false;
        }

        reading = BuildReadingFromDeclaration(candidate, adapter, request.LearnedContextLimits);
        return reading.Models.Count > 0;
    }

    private static DriverCapabilityReading BuildReadingFromDeclaration(
        DriverCapabilityDeclaration declared,
        string expectedAdapter,
        IReadOnlyDictionary<string, int>? learnedLimits)
        => new(
            Adapter: expectedAdapter,
            Models: declared.Models
                .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                .Select(m => new DriverModelCapability(
                    m.Id.Trim(),
                    string.IsNullOrWhiteSpace(m.Name) ? m.Id.Trim() : m.Name!.Trim(),
                    m.Efforts ?? Array.Empty<string>(),
                    declared.SupportsContextSelection
                        ? ContextSizesForModel(m.Id.Trim(), m.MaxContextTokens, learnedLimits)
                        : null))
                .ToList(),
            ModelSource: CapabilitySourceKind.Declared,
            ObservedAt: declared.ObservedAt,
            SupportsContextSelection: declared.SupportsContextSelection,
            EffortSource: $"declared:{declared.Source ?? "runner"}");

    /// <summary>
    /// Prefers the runner's explanation for an empty catalogue over the API's own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Not signed in on this machine" is ground truth about an account. "The agy CLI is not
    /// installed on the API host" is a fact about where the API happens to run, and in a Docker
    /// deployment that is always true, so it would mask the real reason every time. When neither
    /// side produced a model list, the runner's reason is the one worth showing.
    /// </para>
    /// <para>
    /// An explanation is a claim about a driver, so it gets the same scrutiny as a catalogue:
    /// <see cref="TryTrustedDeclaration"/>. Without it, this method overwrote a live probe's failure
    /// with a stale declaration's parting words, or with a message about an adapter the row no
    /// longer uses - both of which send the operator off to fix something that is not broken.
    /// </para>
    /// </remarks>
    private static DriverCapabilityReading? WithDeclaredReason(
        DriverCapabilityReading? reading,
        DriverCapabilityProbeRequest request)
    {
        if (reading is not { Models.Count: 0 }) return reading;

        if (!TryTrustedDeclaration(request, DateTimeOffset.UtcNow, out var declared, out _)) return reading;

        var reason = declared.UnavailableReason;
        if (string.IsNullOrWhiteSpace(reason)) return reading;

        return reading with { UnavailableReason = reason };
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

            var contextSizes = reading.SupportsContextSelection
                ? ContextSizesForModel(id, null, request.LearnedContextLimits)
                : null;

            merged.Add(existing ?? new DriverModelCapability(id, id, Array.Empty<string>(), contextSizes));
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
