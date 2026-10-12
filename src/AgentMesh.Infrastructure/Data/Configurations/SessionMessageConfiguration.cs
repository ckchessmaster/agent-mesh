using AgentMesh.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentMesh.Infrastructure.Data.Configurations;

public class SessionMessageConfiguration : IEntityTypeConfiguration<SessionMessage>
{
    public void Configure(EntityTypeBuilder<SessionMessage> builder)
    {
        builder.ToTable("session_messages");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.SenderAgentId)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(m => m.TargetAgentId)
            .HasMaxLength(64);

        builder.Property(m => m.Content)
            .IsRequired();

        builder.Property(m => m.StructuredPayloadJson)
            .HasColumnName("structured_payload")
            .HasColumnType("jsonb");

        builder.Property(m => m.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.HasIndex(m => m.SessionId)
            .HasDatabaseName("idx_session_messages_session");

        builder.HasOne(m => m.Session)
            .WithMany(s => s.Messages)
            .HasForeignKey(m => m.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.SenderAgent)
            .WithMany(a => a.SentMessages)
            .HasForeignKey(m => m.SenderAgentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(m => m.TargetAgent)
            .WithMany()
            .HasForeignKey(m => m.TargetAgentId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);
    }
}
