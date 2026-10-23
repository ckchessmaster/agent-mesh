using AgentMesh.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentMesh.Infrastructure.Data.Configurations;

public class JobWorkspaceStateConfiguration : IEntityTypeConfiguration<JobWorkspaceState>
{
    public void Configure(EntityTypeBuilder<JobWorkspaceState> builder)
    {
        builder.ToTable("job_workspace_states");
        builder.HasKey(s => s.JobId);

        builder.Property(s => s.ProjectId)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(s => s.BundleS3Key)
            .HasMaxLength(512);

        builder.Property(s => s.GitRemoteUrl)
            .HasMaxLength(512);

        builder.Property(s => s.GitRef)
            .HasMaxLength(128);

        builder.Property(s => s.HeadCommit)
            .HasMaxLength(64);

        builder.Property(s => s.ContentHash)
            .HasMaxLength(64);

        builder.Property(s => s.LastSavedByPersonaId)
            .HasMaxLength(64);

        builder.Property(s => s.LastSavedByAgentId)
            .HasMaxLength(64);

        builder.Property(s => s.LeaseHolderAgentId)
            .HasMaxLength(64);

        builder.Property(s => s.LeaseHolderPersonaId)
            .HasMaxLength(64);

        builder.Property(s => s.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.Property(s => s.UpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        // Look up "which jobs did this agent last touch" — used to find orphans on runner death.
        builder.HasIndex(s => s.LeaseHolderAgentId)
            .HasDatabaseName("ix_job_workspace_states_lease_holder");

        builder.HasIndex(s => s.LastSavedByAgentId)
            .HasDatabaseName("ix_job_workspace_states_last_saved_agent");

        builder.Ignore(s => s.Job);
    }
}
