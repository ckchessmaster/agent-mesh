using AgentMesh.Core.Models;
using AgentMesh.Core.Services;

namespace AgentMesh.Api.Services;

/// <summary>Tunables for the change reader, resolved from configuration.</summary>
/// <param name="MaxFiles">Cap on paths in one list response.</param>
/// <param name="MaxPatchBytes">
/// Ceiling on one patch body. Deliberately far below what a diff of a vendored dependency tree
/// produces: the patch is rendered in a browser panel, and a 40 MB string is not a reviewable
/// artifact, it is a frozen tab.
/// </param>
/// <param name="DefaultContextLines">Unified-diff context when the caller does not ask. 0 is valid.</param>
/// <param name="MaxContextLines">
/// Ceiling on a caller-supplied context. A caller asking for 10,000 lines is asking for the file, and
/// there is a separate, size-bounded route for that.
/// </param>
/// <param name="GitTimeoutSeconds">Per-<c>git</c>-invocation ceiling; 0 means "the runner's default".</param>
public sealed record JobChangesOptions(
    int MaxFiles = JobChangesPolicy.DefaultFileListLimit,
    int MaxPatchBytes = JobChangesPolicy.DefaultPatchByteLimit,
    int DefaultContextLines = 3,
    int MaxContextLines = 80,
    int GitTimeoutSeconds = 0)
{
    /// <summary>Configuration keys, all optional.</summary>
    public const string MaxFilesKey = "Platform:JobChanges:MaxFiles";
    public const string MaxPatchBytesKey = "Platform:JobChanges:MaxPatchBytes";
    public const string ContextLinesKey = "Platform:JobChanges:ContextLines";
    public const string MaxContextLinesKey = "Platform:JobChanges:MaxContextLines";
    public const string GitTimeoutSecondsKey = "Platform:JobChanges:GitTimeoutSeconds";

    /// <summary>
    /// Reads the options, clamping each configured value into range.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Clamped rather than validated, because a typo in a Helm value must not turn the panel into a
    /// 500 and must not silently disable it either: a nonsensical <c>MaxFiles: 0</c> becomes the
    /// default instead of a permanently empty list, and an absurd <c>MaxPatchBytes</c> is capped rather
    /// than allowed to hand a browser a string it cannot lay out.
    /// </para>
    /// <para>
    /// <c>0</c> is a meaningful configured value for the two context knobs (a caller may want a
    /// hunks-only diff), so presence is tested with the provider's section API rather than by
    /// "non-positive means unset" for those two. Every other knob treats a non-positive value as
    /// unset, because a zero file limit or a zero timeout is not a request anyone means.
    /// </para>
    /// </remarks>
    public static JobChangesOptions From(Microsoft.Extensions.Configuration.IConfiguration? configuration)
    {
        if (configuration is null) return new JobChangesOptions();

        var maxFiles = ReadPositive(configuration[MaxFilesKey], JobChangesPolicy.DefaultFileListLimit);
        var patchBytes = ReadPositive(configuration[MaxPatchBytesKey], JobChangesPolicy.DefaultPatchByteLimit);
        var timeout = ReadPositive(configuration[GitTimeoutSecondsKey], 0);

        return new JobChangesOptions(
            MaxFiles: Math.Clamp(maxFiles, 1, JobChangesPolicy.MaxFileListLimit),
            MaxPatchBytes: Math.Clamp(patchBytes, 1024, JobChangesPolicy.MaxPatchByteLimit),
            DefaultContextLines: Math.Clamp(ReadInteger(configuration[ContextLinesKey], 3), 0, 500),
            MaxContextLines: Math.Clamp(ReadInteger(configuration[MaxContextLinesKey], 80), 0, 500),
            GitTimeoutSeconds: Math.Clamp(timeout, 0, 300));
    }

    /// <summary>
    /// Clamps a caller's context request into <see cref="MaxContextLines"/>.
    /// </summary>
    /// <remarks>
    /// A negative value is treated as "not supplied" rather than clamped to 0. Zero context is a real
    /// request (a hunks-only diff) and is honoured when asked for explicitly; <c>-1</c> arriving from a
    /// client that could not read its own default is not that request, and quietly rendering a
    /// hunks-only diff for it would change what the reviewer sees without them asking.
    /// </remarks>
    public int ResolveContext(int? requested) =>
        requested is null or < 0
            ? DefaultContextLines
            // Math.Max is not decoration: Math.Clamp throws ArgumentException when max < min, and
            // although From() clamps the ceiling into 0..500, the record is public and directly
            // constructible, so a negative ceiling must not turn a diff request into an exception.
            : Math.Clamp(requested.Value, 0, Math.Max(MaxContextLines, 0));

    /// <summary>A caller's file-count request, clamped into the policy's range and this deployment's cap.</summary>
    public int ResolveFileLimit(int? requested)
    {
        var ceiling = Math.Clamp(MaxFiles, 1, JobChangesPolicy.MaxFileListLimit);
        return requested is null or <= 0 ? Math.Min(ceiling, JobChangesPolicy.DefaultFileListLimit) : Math.Min(requested.Value, ceiling);
    }

    private static int ReadPositive(string? raw, int fallback) =>
        int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : fallback;

    /// <summary>
    /// Reads a knob for which <c>0</c> is a meaningful value, so "unset" cannot be inferred from
    /// "non-positive".
    /// </summary>
    /// <remarks>
    /// Zero is honoured (a hunks-only diff is a real request) and a negative value falls back, because
    /// <c>-1</c> is what a client that failed to read its own default sends — and the clamp downstream
    /// would otherwise silently turn it into <c>0</c>, changing the diff a reviewer is looking at
    /// without anyone asking it to.
    /// </remarks>
    private static int ReadInteger(string? raw, int fallback) =>
        int.TryParse(raw, out var parsed) && parsed >= 0 ? parsed : fallback;
}

/// <summary>
/// The short-lived reuse store behind the change reader.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a singleton rather than a dictionary on the service.</strong>
/// <see cref="JobChangesService"/> has to be scoped because it depends on
/// <c>AgentMeshDbContext</c>. A dictionary on a scoped service is a fresh empty dictionary per request:
/// it would cost a lock, an expiry rule and review attention while being structurally incapable of
/// reusing anything. Making the store a singleton is the only way the ten-second window is real.
/// </para>
/// <para>
/// <strong>Keyed on what was measured, never on who asked.</strong> The checkout or repository, the
/// branch pair, the diff knobs, the path. Two readers asking the same question about the same commits
/// get the same answer, which is the only reason an entry may be shared at all. An entry is never
/// reused across a caller that pinned SHAs, because that caller asked about a specific pair and the
/// response prints them — see <see cref="JobChangesPolicy.CacheKey"/>.
/// </para>
/// </remarks>
public interface IJobChangesCache
{
    bool TryGet(string key, out JobChangeSetDto value);
    void Set(string key, JobChangeSetDto value, TimeSpan lifetime);
    void Clear();
}

/// <inheritdoc cref="IJobChangesCache"/>
public sealed class JobChangesCache : IJobChangesCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public bool TryGet(string key, out JobChangeSetDto value)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    value = entry.Value;
                    return true;
                }

                _entries.Remove(key);
            }
        }

        value = null!;
        return false;
    }

    /// <remarks>
    /// Bounded by <see cref="JobChangesPolicy.CacheEntryLimit"/>, oldest-first
    /// (<see cref="Entry.StoredAt"/>), with expired entries dropped first. For a ten-second window
    /// this can only ever cost a recomputation, which is the cheap direction to be wrong in: an
    /// unbounded dictionary keyed by job id is a slow leak in a long-lived server process.
    /// </remarks>
    public void Set(string key, JobChangeSetDto value, TimeSpan lifetime)
    {
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            if (_entries.Count >= JobChangesPolicy.CacheEntryLimit && !_entries.ContainsKey(key))
            {
                foreach (var stale in _entries.Where(kvp => kvp.Value.ExpiresAt <= now)
                             .OrderBy(kvp => kvp.Value.ExpiresAt)
                             .Select(kvp => kvp.Key)
                             .Take(Math.Max(1, _entries.Count - JobChangesPolicy.CacheEntryLimit + 1))
                             .ToList())
                {
                    _entries.Remove(stale);
                }

                if (_entries.Count >= JobChangesPolicy.CacheEntryLimit)
                {
                    var oldest = _entries.OrderBy(kvp => kvp.Value.StoredAt).First();
                    _entries.Remove(oldest.Key);
                }
            }

            _entries[key] = new Entry(value, now, now + lifetime);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    private readonly record struct Entry(JobChangeSetDto Value, DateTimeOffset StoredAt, DateTimeOffset ExpiresAt);
}
