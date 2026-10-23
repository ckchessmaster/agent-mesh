using AgentMesh.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentMesh.Infrastructure.Data.Configurations;

public class JobHandoffConfiguration : IEntityTypeConfiguration<JobHandoff>
{
    public void Configure(EntityTypeBuilder<JobHandoff> builder)
    {
        builder.ToTable("job_handoffs");
        builder.HasKey(h => h.Id);

        builder.Property(h => h.JobId)
            .IsRequired();

        builder.Property(h => h.ProjectId)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(h => h.FromPersonaId)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(h => h.ToPersonaId)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(h => h.Status)
            .HasDefaultValue(JobHandoffStatus.Pending)
            .IsRequired();

        builder.Property(h => h.ReleaseReason)
            .IsRequired(false);

        builder.Property(h => h.HandoffNote)
            .HasMaxLength(2048);

        builder.Property(h => h.ExpectedHeadCommit)
            .HasMaxLength(64);

        builder.Property(h => h.Error)
            .HasMaxLength(1024);

        builder.Property(h => h.DeclaredAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.Property(h => h.UpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        // The reaper scans pending handoffs by deadline; the UI reads the open handoff per job.
        builder.HasIndex(h => new { h.Status, h.ReleaseDeadlineAt })
            .HasDatabaseName("ix_job_handoffs_status_deadline");

        builder.HasIndex(h => h.JobId)
            .HasDatabaseName("ix_job_handoffs_job_id");

        // Exactly one open handoff per job. Enforced here as well as in the service, because two
        // concurrent @mentions racing to declare a handoff is a real scenario.
        builder.HasIndex(h => h.JobId)
            .IsUnique()
            .HasDatabaseName("ux_job_handoffs_open_per_job")
            .HasFilter("status IN (0, 1, 2)");

        builder.Ignore(h => h.Job);
    }
}
