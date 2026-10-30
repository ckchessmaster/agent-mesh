namespace AgentMesh.Core.Models;

/// <summary>
/// A single durable lesson an agent learned about an environment, a build/test setup, or a codebase,
/// kept so the next session (or the next pod) does not have to rediscover it.
/// </summary>
/// <remarks>
/// Identity is the triple <c>(AgentId, ProjectId, Key)</c>, enforced by a unique index. That triple is
/// what makes the write path an upsert rather than an insert: an agent refining a lesson re-saves the
/// same key and the row is updated in place, so a stale flag recipe cannot accumulate alongside a new one.
///
/// <see cref="ProjectId"/> carries <see cref="GlobalProjectId"/> for lessons that are not about this
/// repository (a toolchain path, a shell quirk). Reads always widen to include the global bucket, which
/// is why a project-scoped query returns two kinds of row.
/// </remarks>
public class AgentMemory
{
    /// <summary>Sentinel <see cref="ProjectId"/> meaning "applies to every project".</summary>
    public const string GlobalProjectId = "*";

    /// <summary>Column limits, mirrored from the schema so validation fails before a round trip does.</summary>
    public const int MaxAgentIdLength = 64;
    public const int MaxProjectIdLength = 64;
    public const int MaxCategoryLength = 32;
    public const int MaxKeyLength = 128;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// Specific project slug/ID or <see cref="GlobalProjectId"/> for cross-project global memory.
    /// </summary>
    public string ProjectId { get; set; } = GlobalProjectId;

    /// <summary>
    /// One of <see cref="AgentMemoryCategories.All"/>. Stored as text rather than an enum column so the
    /// buckets stay readable in raw SQL and adding one is a code change, not a migration.
    /// </summary>
    public string Category { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// How much to trust this lesson, 0..1. Defaults to 1.0 because the only writer today is the agent
    /// that just verified the finding by hitting it; a future consolidation pass is what lowers it.
    /// </summary>
    public double ConfidenceScore { get; set; } = 1.0;

    public DateTimeOffset CreatedAt { get; set; } = StoreableNow();
    public DateTimeOffset UpdatedAt { get; set; } = StoreableNow();

    /// <summary>
    /// The current time, truncated to the microsecond resolution PostgreSQL stores in a
    /// <c>timestamp with time zone</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="DateTimeOffset.UtcNow"/> carries 100-nanosecond ticks; PostgreSQL keeps microseconds. A
    /// timestamp minted here round-trips the database unchanged, which is what makes
    /// <c>returned.CreatedAt == stored.CreatedAt</c> hold. Minted with full precision it does not: the row
    /// an insert returns would differ from the same row read back a moment later, and the tool echoes
    /// <c>updatedAt</c> straight to the caller.
    /// </remarks>
    public static DateTimeOffset StoreableNow()
    {
        var now = DateTimeOffset.UtcNow;
        const long ticksPerMicrosecond = 10;
        return new DateTimeOffset(now.Ticks - (now.Ticks % ticksPerMicrosecond), now.Offset);
    }

    /// <summary>True when this memory is scoped to every project rather than one repository.</summary>
    public bool IsGlobal =>
        string.Equals(ProjectId?.Trim(), GlobalProjectId, StringComparison.Ordinal);

    /// <summary>
    /// Builds a validated memory, normalizing every field to the form that goes on the wire and into the
    /// unique index.
    /// </summary>
    /// <remarks>
    /// Normalization has to happen here rather than in the repository: the identity of a row is
    /// <c>(agent_id, project_id, key)</c>, so if one writer trimmed and another did not, the same lesson
    /// would be stored twice under <c>"dotnet_test_flags"</c> and <c>"dotnet_test_flags "</c> and both
    /// would be injected into the prompt.
    /// <para>
    /// <paramref name="confidenceScore"/> is clamped rather than rejected when out of range, but a
    /// non-finite value is rejected: NaN cannot be ordered in SQL, and confidence is a sort key on the
    /// read path.
    /// </para>
    /// </remarks>
    /// <returns>null when any field is unusable; the caller turns that into a tool-level error message.</returns>
    public static AgentMemory? TryCreate(
        string agentId,
        string? projectId,
        string category,
        string key,
        string value,
        double? confidenceScore = null)
    {
        if (!TryBuildAgentId(agentId, out var normalizedAgentId) ||
            !TryBuildProjectId(projectId, out var normalizedProjectId) ||
            !AgentMemoryCategories.TryNormalize(category, out var normalizedCategory) ||
            !TryBuildKey(key, out var normalizedKey) ||
            !TryBuildValue(value, out var normalizedValue))
        {
            return null;
        }

        var confidence = confidenceScore ?? 1.0;
        if (double.IsNaN(confidence) || double.IsInfinity(confidence))
        {
            return null;
        }

        var now = StoreableNow();
        return new AgentMemory
        {
            AgentId = normalizedAgentId,
            ProjectId = normalizedProjectId,
            Category = normalizedCategory,
            Key = normalizedKey,
            Value = normalizedValue,
            ConfidenceScore = Math.Clamp(confidence, 0.0, 1.0),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Applies an update onto <paramref name="existing"/>: the new value, category and confidence, plus a
    /// fresh <see cref="UpdatedAt"/>. Identity fields and <see cref="CreatedAt"/> are left alone.
    /// </summary>
    /// <remarks>
    /// Category is refreshed on update. A key filed under the wrong bucket is a mistake the agent will
    /// notice on the next read, and refusing to move it would force a delete-then-insert dance that
    /// resets <see cref="CreatedAt"/> and loses the "we learned this weeks ago" signal.
    /// <para>
    /// <paramref name="confidenceScore"/> is deliberately nullable rather than defaulting to
    /// <see cref="ConfidenceScore"/>'s own 1.0: a re-save that says nothing about confidence must keep the
    /// trust the row already earned, not silently promote a half-remembered lesson back to certain.
    /// </para>
    /// </remarks>
    public static bool TryApplyUpdate(
        AgentMemory existing,
        string category,
        string value,
        double? confidenceScore,
        out AgentMemory updated)
    {
        updated = existing;
        if (!AgentMemoryCategories.TryNormalize(category, out var normalizedCategory) ||
            !TryBuildValue(value, out var normalizedValue))
        {
            return false;
        }

        var confidence = confidenceScore ?? existing.ConfidenceScore;
        if (double.IsNaN(confidence) || double.IsInfinity(confidence))
        {
            return false;
        }

        existing.Category = normalizedCategory;
        existing.Value = normalizedValue;
        existing.ConfidenceScore = Math.Clamp(confidence, 0.0, 1.0);
        existing.UpdatedAt = StoreableNow();

        updated = existing;
        return true;
    }

    /// <summary>
    /// The identity this row is looked up by. Whitespace is already normalized by <see cref="TryCreate"/>,
    /// so this is a plain tuple — it exists to keep the repository and the tests agreeing on one definition.
    /// </summary>
    public MemoryIdentity Identity => new(AgentId, ProjectId, Key);

    /// <summary>
    /// Validates an already-materialized row (e.g. one read back from the database) and returns a
    /// human-readable reason for each problem found. Empty means valid.
    /// </summary>
    public IReadOnlyList<string> ValidationErrors()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(AgentId))
        {
            errors.Add($"'{nameof(AgentId)}' is required.");
        }
        else if (AgentId.Length > MaxAgentIdLength)
        {
            errors.Add($"'{nameof(AgentId)}' exceeds {MaxAgentIdLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(ProjectId))
        {
            errors.Add($"'{nameof(ProjectId)}' is required (use '{GlobalProjectId}' for global memory).");
        }
        else if (ProjectId.Length > MaxProjectIdLength)
        {
            errors.Add($"'{nameof(ProjectId)}' exceeds {MaxProjectIdLength} characters.");
        }

        if (!AgentMemoryCategories.TryNormalize(Category, out _))
        {
            errors.Add($"'{nameof(Category)}' must be one of: {AgentMemoryCategories.AllowedValuesHint()}.");
        }

        if (string.IsNullOrWhiteSpace(Key))
        {
            errors.Add($"'{nameof(Key)}' is required.");
        }
        else if (Key.Length > MaxKeyLength)
        {
            errors.Add($"'{nameof(Key)}' exceeds {MaxKeyLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(Value))
        {
            errors.Add($"'{nameof(Value)}' is required.");
        }

        if (double.IsNaN(ConfidenceScore) || double.IsInfinity(ConfidenceScore) || ConfidenceScore < 0.0 || ConfidenceScore > 1.0)
        {
            errors.Add($"'{nameof(ConfidenceScore)}' must be between 0.0 and 1.0.");
        }

        return errors;
    }

    private static bool TryBuildAgentId(string? raw, out string value) =>
        TryBuildRequiredTrimmed(raw, MaxAgentIdLength, out value);

    private static bool TryBuildKey(string? raw, out string value) =>
        TryBuildRequiredTrimmed(raw, MaxKeyLength, out value);

    private static bool TryBuildValue(string? raw, out string value)
    {
        value = (raw ?? string.Empty).Trim();
        return value.Length > 0;
    }

    private static bool TryBuildProjectId(string? raw, out string value)
    {
        var candidate = (raw ?? string.Empty).Trim();
        if (candidate.Length == 0)
        {
            // Absent project means "not about a specific project", which is exactly what the sentinel is for.
            value = GlobalProjectId;
            return true;
        }

        return TryBuildRequiredTrimmed(candidate, MaxProjectIdLength, out value);
    }

    private static bool TryBuildRequiredTrimmed(string? raw, int maxLength, out string value)
    {
        value = (raw ?? string.Empty).Trim();
        return value.Length > 0 && value.Length <= maxLength;
    }
}

/// <summary>
/// The uniqueness key of a memory. Deliberately a record with ordinal members: the upsert path, the
/// concurrency retry path and the tests all need to answer "is this the same lesson?" identically.
/// </summary>
public readonly record struct MemoryIdentity(string AgentId, string ProjectId, string Key);
