using AgentMesh.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentMesh.Infrastructure.Data.Configurations;

public class JobWorkspaceStateConfiguration : IEntityTypeConfiguration<JobWorkspaceState>
{
    public void Configure(EntityTypeBuilder<JobWorkspaceState> builder)
    {
        builder.ToTable("job_workspace_states");

        // Composite key. A job is a checklist and a task is the unit of execution, so one row per
        // job cannot hold two tasks' branch/head/checkpoint without one overwriting the other.
        // See JobWorkspaceState.TaskId for the sentinel that lets pre-existing rows participate.
        builder.HasKey(s => new { s.JobId, s.TaskId });

        builder.Property(s => s.TaskId)
            .HasMaxLength(64)
            .IsRequired()
            .HasDefaultValue(JobWorkspaceState.UnassignedTaskId);

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

        // ── Working checkpoint ────────────────────────────────────────────────────
        // The recovery read is "give me the checkpoint for (job, task)", which the composite PK
        // already serves. The write is per turn boundary, so no additional index is warranted here;
        // the one below serves the *other* direction — "which task was this agent last on" — which
        // a restarted runner asks when it knows its job but not its task.
        builder.HasIndex(s => new { s.JobId, s.CheckpointUpdatedAt })
            .HasDatabaseName("ix_job_workspace_states_checkpoint_recency");

        builder.Property(s => s.CheckpointBranch)
            .HasMaxLength(128);

        builder.Property(s => s.CheckpointStatus)
            .HasMaxLength(32);

        builder.Property(s => s.CheckpointSummary)
            .HasMaxLength(4096);

        // jsonb, not text: the value is a JSON array and an operator debugging a stuck job reads it
        // with `-> ` operators. Npgsql maps string -> jsonb only when the column type is stated.
        builder.Property(s => s.CheckpointModifiedFilesJson)
            .HasColumnType("jsonb");

        builder.Property(s => s.CheckpointActiveStash)
            .HasMaxLength(512);

        builder.Property(s => s.CheckpointHeadCommit)
            .HasMaxLength(64);

        builder.Property(s => s.CheckpointFocus)
            .HasMaxLength(256);

        builder.Property(s => s.CheckpointUpdatedAt)
            .HasColumnType("timestamp with time zone");

        builder.Ignore(s => s.HasCheckpoint);
        builder.Ignore(s => s.Job);
    }
}
