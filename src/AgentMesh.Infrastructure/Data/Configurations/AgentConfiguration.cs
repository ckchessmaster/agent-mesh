using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentMesh.Infrastructure.Data.Configurations;

public class AgentConfiguration : IEntityTypeConfiguration<Agent>
{
    public void Configure(EntityTypeBuilder<Agent> builder)
    {
        builder.ToTable("agents");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(a => a.Name)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(a => a.Role)
            .HasMaxLength(128)
            .HasDefaultValue("Autonomous Agent")
            .IsRequired();

        builder.Property(a => a.Status)
            .HasMaxLength(32)
            .HasDefaultValue("online")
            .IsRequired();

        builder.Property(a => a.RuntimeType)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(a => a.TrustTier)
            .HasDefaultValue(TrustTier.Intern)
            .IsRequired();

        builder.Property(a => a.PersonaPrompt)
            .IsRequired();

        builder.Property(a => a.MemorySummary)
            .HasDefaultValue(string.Empty)
            .IsRequired();

        builder.Property(a => a.CapabilitiesJson)
            .HasColumnName("capabilities")
            .HasColumnType("jsonb")
            .HasDefaultValue("[]")
            .IsRequired();

        builder.Property(a => a.ApiKeyHash)
            .HasMaxLength(128);

        builder.Property(a => a.LastHeartbeatAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.Property(a => a.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.Property(a => a.UpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.Ignore(a => a.SentMessages);
    }
}
