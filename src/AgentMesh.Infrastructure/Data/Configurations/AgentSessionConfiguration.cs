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

        builder.Property(s => s.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.Property(s => s.UpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();
    }
}
