using AgentMesh.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentMesh.Infrastructure.Data.Configurations;

/// <summary>
/// Mapping for <see cref="PersonaMcpServer"/> onto <c>persona_mcp_servers</c>.
/// </summary>
public class PersonaMcpServerConfiguration : IEntityTypeConfiguration<PersonaMcpServer>
{
    public void Configure(EntityTypeBuilder<PersonaMcpServer> builder)
    {
        builder.ToTable("persona_mcp_servers");
        builder.HasKey(pm => new { pm.PersonaId, pm.McpServerId });

        builder.Property(pm => pm.PersonaId)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(pm => pm.McpServerId)
            .HasMaxLength(64)
            .IsRequired();

        // Omit HasDefaultValue(true) so EF Core never mistakes `Enabled = false` for an unset
        // sentinel and skips writing `enabled` on INSERT.
        builder.Property(pm => pm.Enabled)
            .IsRequired();

        builder.Property(pm => pm.EnvOverridesEncrypted)
            .HasColumnType("text");

        builder.Property(pm => pm.HeadersOverridesEncrypted)
            .HasColumnType("text");

        builder.Property(pm => pm.AllowedToolsJson)
            .HasColumnName("allowed_tools")
            .HasColumnType("jsonb");

        builder.Property(pm => pm.BlockedToolsJson)
            .HasColumnName("blocked_tools")
            .HasColumnType("jsonb");

        builder.Property(pm => pm.AssignedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.Property(pm => pm.UpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.HasIndex(pm => pm.PersonaId)
            .HasDatabaseName("ix_persona_mcp_servers_persona_id");

        builder.HasIndex(pm => pm.McpServerId)
            .HasDatabaseName("ix_persona_mcp_servers_mcp_server_id");

        builder.HasOne(pm => pm.Persona)
            .WithMany(p => p.PersonaMcpServers)
            .HasForeignKey(pm => pm.PersonaId)
            .HasConstraintName("fk_persona_mcp_servers_personas_persona_id")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(pm => pm.McpServer)
            .WithMany(m => m.PersonaMcpServers)
            .HasForeignKey(pm => pm.McpServerId)
            .HasConstraintName("fk_persona_mcp_servers_mcp_servers_mcp_server_id")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
