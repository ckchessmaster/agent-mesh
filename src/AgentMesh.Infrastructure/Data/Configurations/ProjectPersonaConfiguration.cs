using AgentMesh.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentMesh.Infrastructure.Data.Configurations;

public class ProjectPersonaConfiguration : IEntityTypeConfiguration<ProjectPersona>
{
    public void Configure(EntityTypeBuilder<ProjectPersona> builder)
    {
        builder.ToTable("project_personas");
        builder.HasKey(pp => new { pp.ProjectId, pp.PersonaId });

        builder.Property(pp => pp.ProjectId)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(pp => pp.PersonaId)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(pp => pp.AssignedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.HasIndex(pp => pp.ProjectId)
            .HasDatabaseName("ix_project_personas_project_id");

        builder.HasIndex(pp => pp.PersonaId)
            .HasDatabaseName("ix_project_personas_persona_id");

        builder.Ignore(pp => pp.Project);
        builder.Ignore(pp => pp.Persona);
    }
}
