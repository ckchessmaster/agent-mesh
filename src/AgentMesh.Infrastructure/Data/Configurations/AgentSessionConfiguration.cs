using AgentMesh.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentMesh.Infrastructure.Data.Configurations;

public class AgentSessionConfiguration : IEntityTypeConfiguration<AgentSession>
{
    public void Configure(EntityTypeBuilder<AgentSession> builder)
    {
        builder.ToTable("agent_sessions");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Title)
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(s => s.Status)
            .HasMaxLength(32)
            .HasDefaultValue("active")
            .IsRequired();

        builder.Property(s => s.MetadataJson)
            .HasColumnName("metadata")
            .HasColumnType("jsonb")
            .HasDefaultValue("{}")
            .IsRequired();

        builder.Property(s => s.CreatorAgentId)
            .HasMaxLength(64);

        builder.Property(s => s.DriverId)
            .HasMaxLength(64);

        builder.Property(s => s.PersonaId)
            .HasMaxLength(64);

        builder.HasIndex(s => s.CreatorAgentId)
            .HasDatabaseName("ix_agent_sessions_creator_agent_id");

        builder.HasIndex(s => s.DriverId)
            .HasDatabaseName("ix_agent_sessions_driver_id");

        builder.HasIndex(s => s.PersonaId)
            .HasDatabaseName("ix_agent_sessions_persona_id");

        builder.Ignore(s => s.CreatorAgent);
        builder.Ignore(s => s.Driver);
        builder.Ignore(s => s.Persona);

        builder.Property(s => s.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.Property(s => s.UpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();
    }
}
