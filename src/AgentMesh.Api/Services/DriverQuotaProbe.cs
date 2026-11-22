using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using AgentMesh.Core.Services;

namespace AgentMesh.Api.Services;

/// <summary>
/// A single quota window reported by a driver's upstream provider.
/// </summary>
public sealed record DriverQuotaBucket(
    string Id,
    string Name,
    string? Description,
    string? Window,
    double RemainingFraction,
    DateTimeOffset? ResetsAt);

/// <summary>
/// A quota group (e.g. one model family) exactly as the provider grouped it.
/// </summary>
public sealed record DriverQuotaGroup(
    string Name,
    string? Description,
    IReadOnlyList<DriverQuotaBucket> Buckets);

/// <summary>
/// A reading taken from a driver's own provider introspection channel.
/// Nothing here is synthesised: every field was parsed out of a provider response.
/// </summary>
public sealed record DriverQuotaReading(
    string Provider,
    string PlanName,
    string Source,
    DateTimeOffset ObservedAt,
    double PrimaryRemainingFraction,
    IReadOnlyList<DriverQuotaGroup> Groups);

public sealed record DriverQuotaProbeRequest(
    string DriverId,
    RuntimeType RuntimeType,
    string? Adapter,
    string? Endpoint,
    /// <summary>
    /// Bypass the probe's own TTL cache. The dashboard sets this on an explicit user refresh.
    /// </summary>
    bool ForceRefresh = false,
    string? ApiKey = null);

/// <summary>
/// Reads real quota data out of a driver's provider.
///
/// Contract: return <c>null</c> whenever the provider cannot be reached or does not expose a
/// quota surface. The dashboard renders "not available" for null. Implementations must never
/// return a plausible-looking default: a fabricated meter is indistinguishable from a real one
/// to the user, which is worse than no meter at all.
/// </summary>
public interface IDriverQuotaProbe
{
    Task<DriverQuotaReading?> ProbeAsync(DriverQuotaProbeRequest request, CancellationToken ct = default);
}

/// <summary>
/// No-op probe. Used by hosts that do not want the API shelling out, and by tests.
/// </summary>
public sealed class NullDriverQuotaProbe : IDriverQuotaProbe
{
    public static readonly NullDriverQuotaProbe Instance = new();

    public Task<DriverQuotaReading?> ProbeAsync(DriverQuotaProbeRequest request, CancellationToken ct = default)
        => Task.FromResult<DriverQuotaReading?>(null);
}

/// <summary>
/// Probes the Antigravity CLI quota surface with <c>agy -p /usage --output-format json</c>.
/// </summary>
public sealed class AgyDriverQuotaProbe : IDriverQuotaProbe
{
    internal const string ProbeArguments = "-p /usage --output-format json";

    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a "no reading" result is remembered. Longer than the positive TTL on purpose:
    /// an unauthenticated <c>agy</c> blocks for up to 60s waiting for an OAuth code in the terminal
    /// (verified against the real binary; <c>--print-timeout</c> and a closed stdin do not shorten
    /// it), so re-probing every minute would repeatedly burn a full kill-timeout per driver.
    /// </summary>
    private static readonly TimeSpan DefaultNegativeTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Hard ceiling on a single probe. The CLI's own auth wait exceeds this, so the process is
    /// killed rather than allowed to run to completion.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, (DateTimeOffset ObservedAt, DriverQuotaReading? Reading)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// In-flight reads, keyed like <see cref="_cache"/>. A reading is host-level, so the dashboard's
    /// per-driver fan-out must share one CLI process rather than spawn one per driver.
    /// </summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DriverQuotaReading?>> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<TimeSpan> _ttlProvider;
    private readonly Func<TimeSpan> _negativeTtlProvider;
    private readonly Func<string?> _executableResolver;
    private readonly Func<string, string, TimeSpan, CancellationToken, Task<string?>> _runner;

    public AgyDriverQuotaProbe()
        : this(() => DefaultTtl, () => DefaultNegativeTtl, AgyExecutableLocator.Resolve, RunCliAsync)
    {
    }

    internal AgyDriverQuotaProbe(
        Func<TimeSpan> ttlProvider,
        Func<string?> executableResolver,
        Func<string, string, TimeSpan, CancellationToken, Task<string?>> runner)
        : this(ttlProvider, () => DefaultNegativeTtl, executableResolver, runner)
    {
    }

    internal AgyDriverQuotaProbe(
        Func<TimeSpan> ttlProvider,
        Func<TimeSpan> negativeTtlProvider,
        Func<string?> executableResolver,
        Func<string, string, TimeSpan, CancellationToken, Task<string?>> runner)
    {
        _ttlProvider = ttlProvider;
        _negativeTtlProvider = negativeTtlProvider;
        _executableResolver = executableResolver;
        _runner = runner;
    }

    public async Task<DriverQuotaReading?> ProbeAsync(DriverQuotaProbeRequest request, CancellationToken ct = default)
    {
        if (!IsAntigravity(request)) return null;

        var executable = _executableResolver();
        if (string.IsNullOrWhiteSpace(executable))
        {
            // The CLI is not installed on the API host, so there is no quota surface to read.
            // Returning null (rather than a default) is what makes the panel say "not available".
            return null;
        }

        // Keyed by executable, not driver: `agy /usage` reports the quota of the account signed in
        // on *this host*, so every agy driver on the API host genuinely shares one reading.
        var cacheKey = executable;

        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            var allowedAge = cached.Reading == null ? _negativeTtlProvider() : _ttlProvider();
            if (!request.ForceRefresh && DateTimeOffset.UtcNow - cached.ObservedAt < allowedAge)
            {
                return cached.Reading;
            }
        }

        // Single-flight. The dashboard fans this call out over every agy driver on load, and each
        // one resolves to the same host-level reading; without this, N drivers spawn N concurrent
        // `agy` processes. That is wasteful when the CLI answers, and pathological when it does
        // not: an unauthenticated `agy` blocks for up to 60s on an OAuth prompt, so a cold-cache
        // page load used to burn N stalled probe slots on the same question.
        var leaderSource = new TaskCompletionSource<DriverQuotaReading?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = _inFlight.GetOrAdd(cacheKey, leaderSource);

        if (!ReferenceEquals(existing, leaderSource))
        {
            // Awaiting the shared read with *this* caller's token: if the browser goes away, the
            // read still completes for the drivers that stayed. The token only decides whether this
            // caller waits for the answer.
            return await AwaitSharedReadingAsync(existing.Task, ct);
        }

        DriverQuotaReading? reading = null;
        try
        {
            // Deliberately not the caller's token: this read is host-level and shared, so binding
            // it to whichever request happened to arrive first would let one disconnect cancel
            // everyone else's reading. RunCliAsync enforces its own hard timeout instead.
            var raw = await _runner(executable, ProbeArguments, ProbeTimeout, CancellationToken.None);
            reading = Parse(raw);
        }
        catch (Exception)
        {
            // Every failure mode collapses to "no reading", including an OperationCanceledException
            // from the runner's own timeout: the leader no longer carries a caller's token, so a
            // cancellation here means the probe itself gave up. The flight must always complete - a
            // leader that faulted or hung would strand every driver waiting behind it.
            reading = null;
        }
        finally
        {
            // Negative results are cached too (for longer, see DefaultNegativeTtl): a host without
            // a signed-in CLI must not be re-spawned on every dashboard poll.
            _cache[cacheKey] = (DateTimeOffset.UtcNow, reading);

            // Order matters: publish the cache before releasing waiters, and drop the in-flight
            // entry last, so a caller arriving mid-handoff either sees the fresh cache or joins a
            // flight that is still running. A leaked entry would pin the probe to a completed task
            // and the panel would stop ageing; a faulted task would poison every later caller.
            leaderSource.SetResult(reading);
            _inFlight.TryRemove(cacheKey, out _);
        }

        return reading;
    }

    /// <summary>
    /// Waits for a flight owned by another request. Cancellation only detaches this caller; the
    /// shared read keeps running for the drivers that stayed.
    /// </summary>
    private static async Task<DriverQuotaReading?> AwaitSharedReadingAsync(
        Task<DriverQuotaReading?> flight,
        CancellationToken ct)
    {
        if (!ct.CanBeCanceled) return await flight;

        // WaitAsync (unlike WhenAny+Delay) does not propagate cancellation into `flight`, and it
        // does not leave a timer registered for the process lifetime when the flight wins.
        return await flight.WaitAsync(ct);
    }

    /// <summary>
    /// Parses the provider payload. Returns null unless at least one quota window was actually
    /// reported, so an unauthenticated or unexpected response surfaces as "no data" instead of
    /// a full bar.
    /// </summary>
    internal static DriverQuotaReading? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // Non-JSON output (login prompt, usage banner, crash trace) is not a reading.
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // The CLI wraps command output as { command: { data: { ... } } }.
            var unwrapped = Unwrap(root);
            if (unwrapped == null) return null;
            var payload = unwrapped.Value;

            var groups = ReadGroups(payload);
            if (groups.Count == 0) return null;

            var primary = PickPrimaryFraction(groups);

            return new DriverQuotaReading(
                Provider: "Google Antigravity",
                PlanName: ReadString(payload, "plan", "planName", "plan_name", "tier") ?? "Provider plan",
                Source: "agy:/usage",
                ObservedAt: DateTimeOffset.UtcNow,
                PrimaryRemainingFraction: primary,
                Groups: groups);
        }
    }

    private static JsonElement? Unwrap(JsonElement root)
    {
        if (TryGet(root, out var cmd, "command") && cmd.ValueKind == JsonValueKind.Object)
        {
            if (TryGet(cmd, out var data, "data") && data.ValueKind == JsonValueKind.Object)
            {
                return data;
            }

            return cmd;
        }

        if (TryGet(root, out var rootData, "data") && rootData.ValueKind == JsonValueKind.Object)
        {
            return rootData;
        }

        return root;
    }

    private static List<DriverQuotaGroup> ReadGroups(JsonElement payload)
    {
        // An explicit "this account has no quota windows" is a real answer, but not one the panel
        // can render, so it is reported as "nothing to show" rather than as a full bar.
        if (ReadBoolean(payload, "rootQuotaEnabled", "root_quota_enabled") == false)
        {
            return new List<DriverQuotaGroup>();
        }

        var groups = new List<DriverQuotaGroup>();

        if (TryGet(payload, out var groupsElem, "groups", "quotaGroups", "quota_groups") &&
            groupsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in groupsElem.EnumerateArray())
            {
                if (g.ValueKind != JsonValueKind.Object) continue;

                var name = ReadString(g, "name", "title", "label") ?? "Quota";
                var description = ReadString(g, "description", "subtitle");
                var groupWindow = ReadString(g, "window");
                var buckets = new List<DriverQuotaBucket>();

                if (TryGet(g, out var bucketsElem, "buckets", "windows", "limits") &&
                    bucketsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var b in bucketsElem.EnumerateArray())
                    {
                        AddBucket(buckets, b, idFallback: $"{name}-{buckets.Count + 1}", nameFallback: name,
                            windowFallback: groupWindow);
                    }
                }
                else
                {
                    // A group that *is* the window: { name: "Gemini", remaining_fraction: 0.4 }
                    AddBucket(buckets, g, idFallback: name.ToLowerInvariant(), nameFallback: name,
                        windowFallback: groupWindow);
                }

                if (buckets.Count > 0)
                {
                    groups.Add(new DriverQuotaGroup(name, description, buckets));
                }
            }

            if (groups.Count > 0) return groups;
        }

        // Named quota surfaces, the shape the pre-existing parser read:
        //   { planQuota: { buckets: [...] }, currentQuota: { buckets: [...] } }
        foreach (var (key, label) in new[]
                 {
                     ("planQuota", "Plan Quota"), ("plan_quota", "Plan Quota"),
                     ("currentQuota", "Current Quota"), ("current_quota", "Current Quota")
                 })
        {
            if (!TryGet(payload, out var surface, key)) continue;

            var buckets = new List<DriverQuotaBucket>();

            if (surface.ValueKind == JsonValueKind.Object &&
                TryGet(surface, out var inner, "buckets", "windows", "limits") &&
                inner.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in inner.EnumerateArray())
                {
                    AddBucket(buckets, b, idFallback: $"{key}-{buckets.Count + 1}", nameFallback: label,
                        windowFallback: ReadString(surface, "window"));
                }
            }
            else
            {
                AddBucket(buckets, surface, idFallback: key, nameFallback: label, windowFallback: null);
            }

            if (buckets.Count > 0)
            {
                groups.Add(new DriverQuotaGroup(label, null, buckets));
            }
        }

        // Per-model windows: { modelQuotas: { "gemini-4-pro": { remaining_fraction, reset_time } } }
        if (TryGet(payload, out var modelQuotas, "modelQuotas", "model_quotas") &&
            modelQuotas.ValueKind == JsonValueKind.Object)
        {
            var buckets = new List<DriverQuotaBucket>();
            foreach (var prop in modelQuotas.EnumerateObject())
            {
                AddBucket(buckets, prop.Value, idFallback: prop.Name, nameFallback: prop.Name, windowFallback: null);
            }

            if (buckets.Count > 0)
            {
                groups.Add(new DriverQuotaGroup("Per Model", null, buckets));
            }
        }

        if (groups.Count > 0) return groups;

        // Flat shape: { buckets: [...] } or a single window on the payload itself.
        var flat = new List<DriverQuotaBucket>();

        if (TryGet(payload, out var flatBuckets, "buckets", "windows", "limits") &&
            flatBuckets.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in flatBuckets.EnumerateArray())
            {
                AddBucket(flat, b, idFallback: $"window-{flat.Count + 1}", nameFallback: "Quota window", windowFallback: null);
            }
        }
        else
        {
            AddBucket(flat, payload, idFallback: "default", nameFallback: "Quota window", windowFallback: null);
        }

        if (flat.Count > 0)
        {
            groups.Add(new DriverQuotaGroup("Quota", null, flat));
        }

        return groups;
    }

    private static void AddBucket(
        List<DriverQuotaBucket> buckets,
        JsonElement el,
        string idFallback,
        string nameFallback,
        string? windowFallback)
    {
        if (el.ValueKind != JsonValueKind.Object) return;

        // A window must actually report a remaining fraction. Defaulting a missing field to 1.0
        // is what produced the "everything is fine" bars on an unauthenticated CLI.
        var fraction = ReadDouble(el, "remainingFraction", "remaining_fraction", "percentRemaining", "percent_remaining");
        if (fraction == null) return;

        var id = ReadString(el, "id", "key", "window") ?? idFallback;
        var name = ReadString(el, "name", "label", "title") ?? nameFallback;
        var description = ReadString(el, "description", "detail", "hint");
        var window = ReadString(el, "window", "period", "scope") ?? windowFallback;
        var resetsAt = ReadDateTimeOffset(el, "resetTime", "reset_time", "resetsAt", "resets_at");

        buckets.Add(new DriverQuotaBucket(
            Id: id,
            Name: name,
            Description: description,
            Window: window,
            RemainingFraction: ClampFraction(fraction),
            ResetsAt: resetsAt));
    }

    /// <summary>
    /// The headline meter. Prefers the named weekly window (the one users actually run out of),
    /// then the scarcest window reported, so the panel never over-reports headroom.
    /// </summary>
    private static double PickPrimaryFraction(IReadOnlyList<DriverQuotaGroup> groups)
    {
        var all = groups.SelectMany(g => g.Buckets).ToList();
        if (all.Count == 0) return 0;

        var weekly = all.FirstOrDefault(b =>
            Contains(b.Id, "weekly") || Contains(b.Window, "weekly") || Contains(b.Name, "weekly"));

        return (weekly ?? all.OrderBy(b => b.RemainingFraction).First()).RemainingFraction;
    }

    private static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrWhiteSpace(haystack) &&
        haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    private static double ClampFraction(double? fraction)
    {
        if (fraction == null) return 0;
        var v = fraction.Value;

        // Providers alternate between 0-1 fractions and 0-100 percentages.
        if (v > 1.0 && v <= 100.0) v /= 100.0;
        if (v < 0) v = 0;
        if (v > 1) v = 1;

        return Math.Round(v, 4);
    }

    /// <summary>
    /// True only when the driver is known to be backed by Antigravity.
    /// </summary>
    /// <remarks>
    /// <c>RuntimeType.CliAgy</c> is the default for *every* CLI driver in this schema (copilot and
    /// claude rows are created with it too), so it cannot identify a provider on its own. The
    /// adapter recorded in the capabilities blob is the real discriminator; runtime and driver id
    /// are only consulted when no adapter was ever persisted.
    /// </remarks>
    internal static bool IsAntigravity(DriverQuotaProbeRequest request)
    {
        var adapter = request.Adapter?.Trim().ToLowerInvariant();

        if (!string.IsNullOrEmpty(adapter))
        {
            return adapter is "agy" or "antigravity";
        }

        // An endpoint means it speaks OpenAI-compatible HTTP, not the agy CLI. Non-CLI runtimes
        // have no local binary to invoke at all.
        if (!string.IsNullOrWhiteSpace(request.Endpoint)) return false;
        if (request.RuntimeType is RuntimeType.OpenAiCompatible or RuntimeType.LocalOllama or RuntimeType.McpCloud or RuntimeType.OpenRouter)
        {
            return false;
        }

        // No adapter recorded (drivers registered before the field existed): fall back to the id.
        var id = request.DriverId?.Trim().ToLowerInvariant() ?? string.Empty;
        return id == "agy" || id.StartsWith("agy-", StringComparison.Ordinal) ||
               id.EndsWith("-agy", StringComparison.Ordinal) || id.Contains("antigravity");
    }

    private static bool? ReadBoolean(JsonElement root, params string[] names)
    {
        if (!TryGet(root, out var el, names)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        if (!TryGet(root, out var el, names)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static double? ReadDouble(JsonElement root, params string[] names)
    {
        if (!TryGet(root, out var el, names)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d)) return d;
        if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), out var parsed)) return parsed;
        return null;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement root, params string[] names)
    {
        if (!TryGet(root, out var el, names)) return null;

        if (el.ValueKind == JsonValueKind.String)
        {
            var text = el.GetString();
            if (DateTimeOffset.TryParse(text, out var parsed)) return parsed.ToUniversalTime();
            if (long.TryParse(text, out var epochSeconds)) return DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
            return null;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var epoch))
        {
            return epoch > 1_000_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                : DateTimeOffset.FromUnixTimeSeconds(epoch);
        }

        return null;
    }

    private static bool TryGet(JsonElement root, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out value)) return true;
        }

        // Tolerate a differently-cased payload without allocating per lookup.
        foreach (var name in names)
        {
            foreach (var prop in root.EnumerateObject())
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

    /// <summary>
    /// Delegates to the shared runner so the quota and capability probes agree on what "the CLI
    /// did not answer" means (non-zero exit, timeout, missing binary).
    /// </summary>
    private static Task<string?> RunCliAsync(
        string executable,
        string arguments,
        TimeSpan timeout,
        CancellationToken ct)
        => DriverCliRunner.RunAsync(executable, arguments, timeout, ct);
}

/// <summary>
/// Locates the <c>agy</c> binary the same way the CLI runner does, so the API and the driver
/// agree on which install is being probed.
/// </summary>
public static class AgyExecutableLocator
{
    public static string? Resolve()
    {
        var configured = Environment.GetEnvironmentVariable("AGENTMESH_AGY_PATH");
        var candidate = string.IsNullOrWhiteSpace(configured) ? "agy" : configured.Trim();

        if (Path.IsPathRooted(candidate)) return File.Exists(candidate) ? candidate : null;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirs = new List<string>();

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        dirs.AddRange(pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        if (!string.IsNullOrWhiteSpace(home))
        {
            dirs.Add(Path.Combine(home, ".local", "bin"));
            dirs.Add(Path.Combine(home, ".gemini", "antigravity-cli", "bin"));
        }

        var extensions = OperatingSystem.IsWindows() ? new[] { "", ".exe", ".cmd", ".bat" } : new[] { "" };

        foreach (var dir in dirs)
        {
            foreach (var ext in extensions)
            {
                try
                {
                    var fullPath = Path.Combine(dir, candidate + ext);
                    if (File.Exists(fullPath)) return Path.GetFullPath(fullPath);
                }
                catch
                {
                    // Invalid PATH segments are not fatal to discovery.
                }
            }
        }

        return null;
    }
}

/// <summary>
/// Chains multiple <see cref="IDriverQuotaProbe"/> implementations, returning the first real reading.
/// </summary>
public sealed class CompositeDriverQuotaProbe(params IDriverQuotaProbe[] probes) : IDriverQuotaProbe
{
    private readonly IReadOnlyList<IDriverQuotaProbe> _probes = probes ?? Array.Empty<IDriverQuotaProbe>();

    public async Task<DriverQuotaReading?> ProbeAsync(DriverQuotaProbeRequest request, CancellationToken ct = default)
    {
        foreach (var probe in _probes)
        {
            var reading = await probe.ProbeAsync(request, ct);
            if (reading != null) return reading;
        }

        return null;
    }
}

/// <summary>
/// Probes OpenRouter's key spending limit (<c>GET /api/v1/key</c>) with fallback to account credits
/// (<c>GET /api/v1/credits</c>, which requires a Management Key and returns 403 for standard keys).
/// Returns <c>null</c> when unconfigured, unauthorized, or unlimited so no quota bar is ever fabricated.
/// </summary>
public sealed class OpenRouterDriverQuotaProbe : IDriverQuotaProbe
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultNegativeTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    {
        Timeout = ProbeTimeout
    };

    private readonly ConcurrentDictionary<string, (DateTimeOffset ObservedAt, DriverQuotaReading? Reading)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<DriverQuotaReading?>> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<TimeSpan> _ttlProvider;
    private readonly Func<TimeSpan> _negativeTtlProvider;
    private readonly Func<string, string, CancellationToken, Task<(int StatusCode, string? Body)>> _httpGet;

    public OpenRouterDriverQuotaProbe()
        : this(() => DefaultTtl, () => DefaultNegativeTtl, SendGetAsync)
    {
    }

    internal OpenRouterDriverQuotaProbe(
        Func<TimeSpan> ttlProvider,
        Func<TimeSpan> negativeTtlProvider,
        Func<string, string, CancellationToken, Task<(int StatusCode, string? Body)>> httpGet)
    {
        _ttlProvider = ttlProvider;
        _negativeTtlProvider = negativeTtlProvider;
        _httpGet = httpGet;
    }

    public async Task<DriverQuotaReading?> ProbeAsync(DriverQuotaProbeRequest request, CancellationToken ct = default)
    {
        if (!IsOpenRouter(request)) return null;

        var apiKey = !string.IsNullOrWhiteSpace(request.ApiKey)
            ? request.ApiKey.Trim()
            : Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var baseUrl = NormalizeBaseUrl(request.Endpoint);
        var keyFingerprint = ComputeKeyFingerprint(apiKey);
        var cacheKey = $"openrouter:{DriverModelDiscovery.NormalizeEndpoint(baseUrl)}:{keyFingerprint}";

        if (!request.ForceRefresh && _cache.TryGetValue(cacheKey, out var cached))
        {
            var allowedAge = cached.Reading == null ? _negativeTtlProvider() : _ttlProvider();
            if (DateTimeOffset.UtcNow - cached.ObservedAt < allowedAge)
            {
                return cached.Reading;
            }
        }

        var leaderSource = new TaskCompletionSource<DriverQuotaReading?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = _inFlight.GetOrAdd(cacheKey, leaderSource);

        if (!ReferenceEquals(existing, leaderSource))
        {
            return ct.CanBeCanceled ? await existing.Task.WaitAsync(ct) : await existing.Task;
        }

        DriverQuotaReading? reading = null;
        try
        {
            reading = await FetchReadingAsync(baseUrl, apiKey, CancellationToken.None);
        }
        catch (Exception)
        {
            reading = null;
        }
        finally
        {
            _cache[cacheKey] = (DateTimeOffset.UtcNow, reading);
            leaderSource.SetResult(reading);
            _inFlight.TryRemove(cacheKey, out _);
        }

        return reading;
    }

    private async Task<DriverQuotaReading?> FetchReadingAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        var (keyStatus, keyBody) = await _httpGet($"{baseUrl}/key", apiKey, ct);
        if (keyStatus is >= 200 and < 300)
        {
            var keyResult = ParseKeyResponse(keyBody, out var hasKeyData);
            if (keyResult != null) return keyResult;
            if (!hasKeyData) return null;

            // Unlimited key (`limit: null`): try account credits endpoint as fallback.
            // Standard API keys receive 403 here (Management Key required), which returns null cleanly.
            var (creditsStatus, creditsBody) = await _httpGet($"{baseUrl}/credits", apiKey, ct);
            if (creditsStatus is >= 200 and < 300)
            {
                return ParseCreditsResponse(creditsBody);
            }

            return null;
        }

        return null;
    }

    internal static DriverQuotaReading? ParseKeyResponse(string? json, out bool hasKeyData)
    {
        hasKeyData = false;
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var payload = root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Object
                ? dataProp
                : root;

            hasKeyData = true;

            var limit = ReadDouble(payload, "limit");
            if (limit is null or <= 0) return null;

            var usage = ReadDouble(payload, "usage") ?? 0.0;
            var limitRemaining = ReadDouble(payload, "limit_remaining") ?? Math.Max(0.0, limit.Value - usage);
            var fraction = Math.Round(Math.Clamp(limitRemaining / limit.Value, 0.0, 1.0), 4);

            var isFreeTier = payload.TryGetProperty("is_free_tier", out var freeProp) && freeProp.ValueKind == JsonValueKind.True;
            var label = ReadString(payload, "label");
            var planBase = isFreeTier ? "Free Tier" : "API Key Budget";
            var planName = !string.IsNullOrWhiteSpace(label) ? $"{planBase} · {label.Trim()}" : planBase;

            var limitReset = ReadString(payload, "limit_reset")?.Trim();
            DateTimeOffset? resetsAt = null;
            if (!string.IsNullOrWhiteSpace(limitReset) &&
                DateTimeOffset.TryParse(limitReset, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedReset))
            {
                resetsAt = parsedReset.ToUniversalTime();
            }

            var description = string.Format(
                CultureInfo.InvariantCulture,
                "${0:0.00} of ${1:0.00} remaining",
                Math.Max(0.0, limitRemaining),
                limit.Value);

            var bucket = new DriverQuotaBucket(
                Id: "key-limit",
                Name: "Key Credit Limit",
                Description: description,
                Window: string.IsNullOrWhiteSpace(limitReset) ? null : limitReset,
                RemainingFraction: fraction,
                ResetsAt: resetsAt);

            return new DriverQuotaReading(
                Provider: "OpenRouter",
                PlanName: planName,
                Source: "openrouter:/key",
                ObservedAt: DateTimeOffset.UtcNow,
                PrimaryRemainingFraction: fraction,
                Groups: new[] { new DriverQuotaGroup("Key Spending Limit", null, new[] { bucket }) });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static DriverQuotaReading? ParseCreditsResponse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var payload = root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Object
                ? dataProp
                : root;

            var totalCredits = ReadDouble(payload, "total_credits");
            if (totalCredits is null or <= 0) return null;

            var totalUsage = ReadDouble(payload, "total_usage") ?? 0.0;
            var remaining = Math.Max(0.0, totalCredits.Value - totalUsage);
            var fraction = Math.Round(Math.Clamp(remaining / totalCredits.Value, 0.0, 1.0), 4);

            var description = string.Format(
                CultureInfo.InvariantCulture,
                "${0:0.00} of ${1:0.00} remaining",
                remaining,
                totalCredits.Value);

            var bucket = new DriverQuotaBucket(
                Id: "account-credits",
                Name: "Account Credits",
                Description: description,
                Window: null,
                RemainingFraction: fraction,
                ResetsAt: null);

            return new DriverQuotaReading(
                Provider: "OpenRouter",
                PlanName: "Account Credits",
                Source: "openrouter:/credits",
                ObservedAt: DateTimeOffset.UtcNow,
                PrimaryRemainingFraction: fraction,
                Groups: new[] { new DriverQuotaGroup("Account Balance", null, new[] { bucket }) });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static bool IsOpenRouter(DriverQuotaProbeRequest request)
    {
        var adapter = DriverAdapters.Canonicalize(request.Adapter);
        if (!string.IsNullOrEmpty(adapter))
        {
            return adapter == DriverAdapters.OpenRouter;
        }

        if (request.RuntimeType == RuntimeType.OpenRouter) return true;

        if (!string.IsNullOrWhiteSpace(request.Endpoint))
        {
            return request.Endpoint.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase);
        }

        if (request.RuntimeType is RuntimeType.OpenAiCompatible or RuntimeType.LocalOllama or RuntimeType.McpCloud)
        {
            return false;
        }

        var id = request.DriverId?.Trim().ToLowerInvariant() ?? string.Empty;
        return id == "openrouter" || id.StartsWith("openrouter-", StringComparison.Ordinal) ||
               id.EndsWith("-openrouter", StringComparison.Ordinal) || id.Contains("openrouter");
    }

    internal static string NormalizeBaseUrl(string? endpoint)
    {
        var trimmed = (!string.IsNullOrWhiteSpace(endpoint)
            ? endpoint
            : Environment.GetEnvironmentVariable("OPENROUTER_BASE_URL"))?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return OpenRouterConfig.DefaultEndpoint;

        var normalized = trimmed.TrimEnd('/');
        if (normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^"/chat/completions".Length].TrimEnd('/');
        if (normalized.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^"/models".Length].TrimEnd('/');
        return normalized;
    }

    private static string ComputeKeyFingerprint(string apiKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(bytes)[..16];
    }

    private static async Task<(int StatusCode, string? Body)> SendGetAsync(
        string url,
        string apiKey,
        CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ProbeTimeout);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            req.Headers.TryAddWithoutValidation("X-Title", OpenRouterConfig.DefaultAppTitle);

            using var res = await SharedHttp.SendAsync(req, timeoutCts.Token);
            var body = await res.Content.ReadAsStringAsync(timeoutCts.Token);
            return ((int)res.StatusCode, body);
        }
        catch
        {
            return (0, null);
        }
    }

    private static string? ReadString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static double? ReadDouble(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var d)) return d;
        if (prop.ValueKind == JsonValueKind.String &&
            double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return null;
    }
}
