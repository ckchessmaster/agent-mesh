using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentMesh.Infrastructure.Data.Configurations;

public class PersonaConfiguration : IEntityTypeConfiguration<Persona>
{
    public void Configure(EntityTypeBuilder<Persona> builder)
    {
        builder.ToTable("personas");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(p => p.Name)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(p => p.Role)
            .HasMaxLength(128)
            .HasDefaultValue("Autonomous Persona")
            .IsRequired();

        builder.Property(p => p.PersonaPrompt)
            .IsRequired();

        builder.Property(p => p.DriverId)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(p => p.DefaultModel)
            .HasMaxLength(128);

        builder.Property(p => p.DefaultEffort)
            .HasMaxLength(32);

        builder.Property(p => p.DefaultMode)
            .HasMaxLength(64);

        builder.Property(p => p.DefaultTier)
            .HasMaxLength(64);

        builder.Property(p => p.DefaultWorkingDirectory)
            .HasMaxLength(512);

        builder.Property(p => p.TrustTier)
            .HasDefaultValue(TrustTier.Intern)
            .IsRequired();

        builder.Property(p => p.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.Property(p => p.UpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.HasIndex(p => p.DriverId)
            .HasDatabaseName("ix_personas_driver_id");

        builder.Ignore(p => p.Driver);
        builder.Ignore(p => p.Sessions);
    }
}
