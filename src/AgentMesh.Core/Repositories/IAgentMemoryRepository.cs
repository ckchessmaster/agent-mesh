using AgentMesh.Core.Models;

namespace AgentMesh.Core.Repositories;

/// <summary>
/// Storage for durable, cross-session agent lessons.
/// </summary>
/// <remarks>
/// The contract is deliberately three methods wide. Memory is written by a tool call in the middle of a
/// turn (so the write must never throw a collision at the agent) and read once at prompt-assembly time
/// (so the read must be a single indexed sweep, not a per-category query fan-out).
/// </remarks>
public interface IAgentMemoryRepository
{
    /// <summary>
    /// Upserts an agent memory by (AgentId, ProjectId, Key).
    /// If it exists, updates Value, ConfidenceScore, and UpdatedAt.
    /// </summary>
    /// <remarks>
    /// The returned instance is the row as stored: on insert it carries the caller's <see cref="AgentMemory.Id"/>
    /// and timestamps, on update it carries the original <see cref="AgentMemory.CreatedAt"/> and the identity
    /// of the row that was actually replaced (which is not necessarily <paramref name="memory"/>'s Id).
    /// Callers that echo the Id back to the agent must read it from the return value.
    /// </remarks>
    Task<AgentMemory> UpsertMemoryAsync(AgentMemory memory, CancellationToken ct = default);

    /// <summary>
    /// Retrieves memories matching the agent and project (including global '*' memories).
    /// Ordered by Category ascending, UpdatedAt descending.
    /// </summary>
    /// <remarks>
    /// A null or blank <paramref name="projectId"/> widens the read to every project the agent has
    /// written to, which is what a prompt-stack renderer wants when it has no project boundary yet.
    /// </remarks>
    Task<IReadOnlyList<AgentMemory>> GetMemoriesForAgentAsync(
        string agentId,
        string? projectId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes a memory record by ID.
    /// </summary>
    /// <returns>false when no row matched, so a caller can tell "already gone" from "deleted".</returns>
    Task<bool> DeleteMemoryAsync(Guid id, CancellationToken ct = default);
}
