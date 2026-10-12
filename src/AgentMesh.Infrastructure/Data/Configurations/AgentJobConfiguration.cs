using AgentMesh.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentMesh.Infrastructure.Data.Configurations;

public class AgentJobConfiguration : IEntityTypeConfiguration<AgentJob>
{
    public void Configure(EntityTypeBuilder<AgentJob> builder)
    {
        builder.ToTable("agent_jobs");
        builder.HasKey(j => j.Id);

        builder.Property(j => j.RequestedByAgentId)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(j => j.TargetService)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(j => j.ActionVerb)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(j => j.PayloadJson)
            .HasColumnName("payload")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(j => j.PayloadS3Uri)
            .HasColumnName("payload_s3_uri")
            .HasMaxLength(512);

        builder.Property(j => j.ApprovalStatus)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(j => j.ExecutionStatus)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(j => j.TemporalWorkflowId)
            .HasMaxLength(128);

        builder.Property(j => j.ResultJson)
            .HasColumnName("result")
            .HasColumnType("jsonb");

        builder.Property(j => j.ResultS3Uri)
            .HasColumnName("result_s3_uri")
            .HasMaxLength(512);

        builder.Property(j => j.ArtifactsManifestJson)
            .HasColumnName("artifacts")
            .HasColumnType("jsonb")
            .HasDefaultValue("{}")
            .IsRequired();

        builder.Property(j => j.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.Property(j => j.UpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.HasIndex(j => j.ExecutionStatus).HasDatabaseName("idx_jobs_status");
        builder.HasIndex(j => j.SessionId).HasDatabaseName("idx_jobs_session");

        builder.HasOne(j => j.Session)
            .WithMany(s => s.Jobs)
            .HasForeignKey(j => j.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(j => j.RequestedByAgent)
            .WithMany(a => a.RequestedJobs)
            .HasForeignKey(j => j.RequestedByAgentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
