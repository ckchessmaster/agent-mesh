using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentMesh.Infrastructure.Data.Configurations;

public class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("projects");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(p => p.Name)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(p => p.Description)
            .HasMaxLength(512)
            .HasDefaultValue(string.Empty)
            .IsRequired();

        builder.Property(p => p.WorkspaceType)
            .HasDefaultValue(WorkspaceType.Git)
            .IsRequired();

        builder.Property(p => p.GitRepositoryUrl)
            .HasMaxLength(512);

        builder.Property(p => p.GitBaseBranch)
            .HasMaxLength(128)
            .HasDefaultValue("main")
            .IsRequired();

        builder.Property(p => p.S3BucketName)
            .HasMaxLength(128);

        builder.Property(p => p.S3PrefixKey)
            .HasMaxLength(512);

        builder.Property(p => p.ProjectInstructions)
            .HasDefaultValue(string.Empty)
            .IsRequired();

        builder.Property(p => p.DefaultLeadPersonaId)
            .HasMaxLength(64);

        builder.Property(p => p.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.Property(p => p.UpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.HasIndex(p => p.DefaultLeadPersonaId)
            .HasDatabaseName("ix_projects_default_lead_persona_id");

        builder.Ignore(p => p.DefaultLeadPersona);
        builder.Ignore(p => p.ProjectPersonas);
        builder.Ignore(p => p.Jobs);
    }
}
