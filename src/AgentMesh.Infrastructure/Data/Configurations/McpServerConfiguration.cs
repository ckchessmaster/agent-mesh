using AgentMesh.Core.Enums;
using AgentMesh.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentMesh.Infrastructure.Data.Configurations;

/// <summary>
/// Mapping for <see cref="McpServer"/> onto <c>mcp_servers</c>.
/// </summary>
public class McpServerConfiguration : IEntityTypeConfiguration<McpServer>
{
    public void Configure(EntityTypeBuilder<McpServer> builder)
    {
        builder.ToTable("mcp_servers");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Id)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(m => m.Name)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(m => m.Description)
            .HasMaxLength(1024)
            .HasDefaultValue(string.Empty)
            .IsRequired();

        builder.Property(m => m.Transport)
            .HasConversion<string>()
            .HasMaxLength(16)
            .HasDefaultValue(McpTransportType.Stdio)
            .HasSentinel((McpTransportType)(-1))
            .IsRequired();

        builder.Property(m => m.Command)
            .HasMaxLength(512);

        builder.Property(m => m.ArgsJson)
            .HasColumnName("args")
            .HasColumnType("jsonb")
            .HasDefaultValue("[]")
            .IsRequired();

        builder.Property(m => m.Url)
            .HasMaxLength(1024);

        builder.Property(m => m.EnvEncrypted)
            .HasColumnType("text");

        builder.Property(m => m.HeadersEncrypted)
            .HasColumnType("text");

        // Omit HasDefaultValue(true) so EF Core never mistakes `Enabled = false` (the CLR bool
        // default) for an unset sentinel and silently persists `true` on INSERT.
        builder.Property(m => m.Enabled)
            .IsRequired();

        builder.Property(m => m.TimeoutSeconds)
            .HasDefaultValue(30)
            .HasSentinel(0)
            .IsRequired();

        builder.Property(m => m.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.Property(m => m.UpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.HasIndex(m => m.Name)
            .HasDatabaseName("ix_mcp_servers_name");
    }
}
