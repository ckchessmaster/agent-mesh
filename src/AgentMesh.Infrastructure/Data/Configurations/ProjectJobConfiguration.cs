using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentMesh.Infrastructure.Data.Configurations;

public class ProjectJobConfiguration : IEntityTypeConfiguration<ProjectJob>
{
    public void Configure(EntityTypeBuilder<ProjectJob> builder)
    {
        builder.ToTable("project_jobs");
        builder.HasKey(j => j.Id);

        builder.Property(j => j.ProjectId)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(j => j.Title)
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(j => j.Description)
            .HasMaxLength(1024)
            .HasDefaultValue(string.Empty)
            .IsRequired();

        builder.Property(j => j.Status)
            .HasDefaultValue(ProjectJobStatus.Pending)
            .HasSentinel((ProjectJobStatus)(-1))
            .IsRequired();

        builder.Property(j => j.Priority)
            .HasDefaultValue(ProjectJobPriority.Normal)
            .HasSentinel((ProjectJobPriority)(-1))
            .IsRequired();

        builder.Property(j => j.GitBranchName)
            .HasMaxLength(128)
            .HasDefaultValue(string.Empty)
            .IsRequired();

        builder.Property(j => j.S3ArchiveKey)
            .HasMaxLength(512);

        builder.Property(j => j.TasksJson)
            .HasColumnName("tasks")
            .HasColumnType("jsonb")
            .HasDefaultValue("[]")
            .IsRequired();

        builder.Property(j => j.QueuedTurnsJson)
            .HasColumnName("queued_turns")
            .HasColumnType("jsonb")
            .HasDefaultValue("[]")
            .IsRequired();

        // Mirrors agent_jobs.artifacts (same column name, same jsonb type, same '{}'→'[]' shape rule):
        // a job's artifact index is read whole and written whole by one caller at a time, so it belongs
        // in a column the way tasks and queued turns do, not in a table the way the project document
        // library does. See AgentMesh.Core.Models.ProjectJob.ArtifactsJson.
        builder.Property(j => j.ArtifactsJson)
            .HasColumnName("artifacts")
            .HasColumnType("jsonb")
            .HasDefaultValue("[]")
            .IsRequired();

        builder.Property(j => j.ResultSummary);

        builder.Property(j => j.ApprovalPolicy)
            .HasColumnName("approval_policy")
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(PlanApprovalPolicy.LeadOrHuman)
            .IsRequired();

        builder.Property(j => j.CurrentTaskId)
            .HasColumnName("current_task_id")
            .HasMaxLength(64);

        builder.Property(j => j.ProgressHeartbeat)
            .HasColumnName("progress_heartbeat")
            .HasColumnType("text");

        builder.Property(j => j.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.Property(j => j.UpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.HasIndex(j => j.ProjectId)
            .HasDatabaseName("ix_project_jobs_project_id");

        builder.HasIndex(j => j.Status)
            .HasDatabaseName("ix_project_jobs_status");

        builder.Ignore(j => j.Project);
        builder.Ignore(j => j.Messages);
    }
}
