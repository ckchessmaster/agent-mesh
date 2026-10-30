using AgentMesh.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentMesh.Infrastructure.Data.Configurations;

/// <summary>
/// Mapping for <see cref="AgentMemory"/>. Applied from
/// <see cref="AgentMeshDbContext.OnModelCreating"/> via <c>ApplyConfigurationsFromAssembly</c>, which is
/// how every other entity in this context is configured.
/// </summary>
/// <remarks>
/// Two things here are load-bearing beyond the obvious column widths:
///
/// <list type="bullet">
///   <item>
///   The unique index on <c>(agent_id, project_id, key)</c> is the whole upsert contract. The repository
///   relies on it to make a concurrent double-save idempotent, so it must exist in the database and not
///   only as an application-side check.
///   </item>
///   <item>
///   The <c>agent_id</c>/<c>project_id</c> index is not redundant with the unique one even though it looks
///   that way: the read path is <c>WHERE agent_id = @x AND (project_id = @y OR project_id = '*')</c>, and
///   the OR stops PostgreSQL from using the unique index's <c>project_id</c> key column as an equality
///   seek. The planner falls back to a scan over the agent's key range, which this index serves directly.
///   </item>
/// </list>
/// </remarks>
public class AgentMemoryConfiguration : IEntityTypeConfiguration<AgentMemory>
{
    public void Configure(EntityTypeBuilder<AgentMemory> builder)
    {
        builder.ToTable("agent_memories");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .ValueGeneratedOnAdd()
            .HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.AgentId)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(e => e.ProjectId)
            .HasMaxLength(64)
            .IsRequired()
            .HasDefaultValue(AgentMemory.GlobalProjectId);

        builder.Property(e => e.Category)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(e => e.Key)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(e => e.Value)
            .IsRequired();

        builder.Property(e => e.ConfidenceScore)
            .HasDefaultValue(1.0d)
            .IsRequired();

        builder.Property(e => e.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.Property(e => e.UpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        // The upsert identity: one live lesson per (agent, project, key).
        builder.HasIndex(e => new { e.AgentId, e.ProjectId, e.Key })
            .IsUnique()
            .HasDatabaseName("uq_agent_project_key");

        // The read path: prompt assembly pulls an agent's project + global rows in one sweep.
        builder.HasIndex(e => new { e.AgentId, e.ProjectId })
            .HasDatabaseName("idx_agent_memories_lookup");
    }
}
