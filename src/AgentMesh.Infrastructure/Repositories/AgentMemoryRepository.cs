using AgentMesh.Core.Models;
using AgentMesh.Core.Repositories;
using AgentMesh.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AgentMesh.Infrastructure.Repositories;

/// <summary>
/// EF Core backed store for <see cref="AgentMemory"/> rows.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why find-and-update instead of <c>ON CONFLICT DO UPDATE</c>:</strong> the spec allows either.
/// The native form was rejected for two concrete reasons. First, it needs <c>ExecuteUpdate</c>/raw SQL,
/// neither of which the in-memory provider supports — and the in-memory provider is what every test
/// harness in <c>tests/AgentMesh.Tests</c> runs on, so a native upsert would leave the repository's
/// central behaviour (the collision path) untested by unit tests. Second, the update needs to preserve
/// <c>created_at</c> while refreshing <c>updated_at</c>, which is easy to express once and hard to keep
/// correct in two places.
/// </para>
/// <para>
/// The race that find-and-update does not close is handled by the unique index instead: two concurrent
/// saves of a brand-new key both miss the lookup, one insert wins, and the loser retries as an update
/// (see <see cref="UpsertMemoryAsync"/>). That makes the operation idempotent under concurrency without
/// a transaction, which is what lets the same code run on both providers.
/// </para>
/// </remarks>
public class AgentMemoryRepository(
    AgentMeshDbContext db,
    ILogger<AgentMemoryRepository>? logger = null) : IAgentMemoryRepository
{
    /// <summary>
    /// How many times a losing insert retries as an update. Two attempts is enough for one loser; the
    /// extra attempts cover a third writer landing between the loser's retry-lookup and its save.
    /// </summary>
    private const int MaxUpsertAttempts = 3;

    public async Task<AgentMemory> UpsertMemoryAsync(AgentMemory memory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(memory);

        var errors = memory.ValidationErrors();
        if (errors.Count > 0)
        {
            throw new ArgumentException(
                $"Cannot save agent memory: {string.Join(' ', errors)}", nameof(memory));
        }

        for (var attempt = 1; ; attempt++)
        {
            var existing = await db.AgentMemories
                .FirstOrDefaultAsync(m =>
                    m.AgentId == memory.AgentId &&
                    m.ProjectId == memory.ProjectId &&
                    m.Key == memory.Key,
                    ct);

            if (existing != null)
            {
                // TryApplyUpdate is the single definition of what an update changes: value, category and
                // confidence refresh, identity and CreatedAt do not. Its result is checked rather than
                // discarded even though `memory` was validated above — a silently ignored false here would
                // save an unmodified row and report success.
                if (!AgentMemory.TryApplyUpdate(
                        existing,
                        memory.Category,
                        memory.Value,
                        memory.ConfidenceScore,
                        out var updated))
                {
                    throw new InvalidOperationException(
                        $"Agent memory '{memory.Key}' passed validation but could not be applied to the stored row; this is a bug in {nameof(AgentMemory)}.TryApplyUpdate.");
                }

                await db.SaveChangesAsync(ct);
                logger?.LogInformation(
                    "Agent memory updated: agent={AgentId} project={ProjectId} key={Key} id={Id}",
                    updated.AgentId, updated.ProjectId, updated.Key, updated.Id);
                return updated;
            }

            var now = AgentMemory.StoreableNow();
            memory.CreatedAt = now;
            memory.UpdatedAt = now;
            db.AgentMemories.Add(memory);

            try
            {
                await db.SaveChangesAsync(ct);
                logger?.LogInformation(
                    "Agent memory inserted: agent={AgentId} project={ProjectId} key={Key} id={Id}",
                    memory.AgentId, memory.ProjectId, memory.Key, memory.Id);
                return memory;
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex) && attempt < MaxUpsertAttempts)
            {
                // Someone else inserted the same (agent, project, key) between our lookup and our save.
                // Drop our tracked entity so the next pass finds theirs and updates it instead.
                Detach(memory);
                logger?.LogDebug(
                    ex,
                    "Agent memory insert collided on key={Key} (attempt {Attempt}); retrying as update",
                    memory.Key, attempt);
            }
        }
    }

    public async Task<IReadOnlyList<AgentMemory>> GetMemoriesForAgentAsync(
        string agentId,
        string? projectId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return [];
        }

        var normalizedAgentId = agentId.Trim();
        var normalizedProjectId = projectId?.Trim();

        var query = db.AgentMemories
            .AsNoTracking()
            .Where(m => m.AgentId == normalizedAgentId);

        // A blank project widens to every project the agent has written to: that is what a prompt-stack
        // renderer wants when it has no project boundary to scope to yet.
        if (!string.IsNullOrEmpty(normalizedProjectId))
        {
            query = query.Where(m => m.ProjectId == normalizedProjectId || m.ProjectId == AgentMemory.GlobalProjectId);
        }

        // Id is the tiebreak so repeated reads of an unchanged set return byte-identical ordering — the
        // prompt stack prepends these rows, and a shuffled prefix costs the driver its cached context.
        return await query
            .OrderBy(m => m.Category)
            .ThenByDescending(m => m.UpdatedAt)
            .ThenBy(m => m.Id)
            .ToListAsync(ct);
    }

    public Task<bool> DeleteMemoryAsync(Guid id, CancellationToken ct = default) =>
        DeleteCoreAsync(id, ct);

    private async Task<bool> DeleteCoreAsync(Guid id, CancellationToken ct)
    {
        var memory = await db.AgentMemories.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (memory == null)
        {
            return false;
        }

        db.AgentMemories.Remove(memory);
        await db.SaveChangesAsync(ct);
        logger?.LogInformation("Agent memory deleted: id={Id} key={Key}", id, memory.Key);
        return true;
    }

    /// <summary>
    /// True when a save failed because the <c>(agent_id, project_id, key)</c> unique index rejected it.
    /// </summary>
    /// <remarks>
    /// The typed check is the one PostgreSQL hits in production: EF wraps the provider exception, and only
    /// <see cref="PostgresException"/> carries the SQLSTATE. The message fallback covers any other provider
    /// that reports a constraint breach without a typed error code, so the retry path is not silently
    /// Npgsql-only if the store ever changes.
    /// </remarks>
    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        HasUniqueViolationCode(exception.InnerException) ||
        (exception.InnerException?.Message ?? exception.Message)
        .Contains("duplicate key", StringComparison.OrdinalIgnoreCase);

    private static bool HasUniqueViolationCode(Exception? exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Stops tracking <paramref name="memory"/> if the failed save left it in the tracker.
    /// </summary>
    /// <remarks>
    /// Guarded because <see cref="Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry"/> throws for an
    /// untracked entity. A save failure normally leaves the row tracked as Added, but the guard means an
    /// unexpected tracker state surfaces as the original 23505 rather than as a confusing secondary
    /// exception from the recovery path itself.
    /// </remarks>
    private void Detach(AgentMemory memory)
    {
        var entry = db.ChangeTracker.Entries<AgentMemory>().FirstOrDefault(e => ReferenceEquals(e.Entity, memory));
        if (entry is not null)
        {
            entry.State = EntityState.Detached;
        }
    }
}
