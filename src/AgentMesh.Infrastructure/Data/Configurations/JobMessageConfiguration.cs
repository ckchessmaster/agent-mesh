using AgentMesh.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentMesh.Infrastructure.Data.Configurations;

public class JobMessageConfiguration : IEntityTypeConfiguration<JobMessage>
{
    public void Configure(EntityTypeBuilder<JobMessage> builder)
    {
        builder.ToTable("job_messages");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.JobId)
            .IsRequired();

        builder.Property(m => m.SenderType)
            .HasMaxLength(32)
            .HasDefaultValue("user")
            .IsRequired();

        builder.Property(m => m.SenderId)
            .HasMaxLength(64);

        builder.Property(m => m.SenderName)
            .HasMaxLength(128);

        builder.Property(m => m.Content)
            .IsRequired();

        builder.Property(m => m.MentionsJson)
            .HasColumnName("mentions")
            .HasColumnType("jsonb")
            .HasDefaultValue("[]")
            .IsRequired();

        builder.Property(m => m.TurnSessionId);

        builder.Property(m => m.StructuredPayloadJson)
            .HasColumnName("structured_payload")
            .HasColumnType("jsonb");

        builder.Property(m => m.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.HasIndex(m => m.JobId)
            .HasDatabaseName("ix_job_messages_job_id");

        builder.HasIndex(m => m.TurnSessionId)
            .HasDatabaseName("ix_job_messages_turn_session_id");

        builder.Ignore(m => m.Job);
    }
}
