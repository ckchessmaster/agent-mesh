using AgentMesh.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AgentMesh.Infrastructure.Data.Configurations;

/// <summary>
/// Mapping for <see cref="ProjectDocument"/> onto <c>project_documents</c>.
/// </summary>
/// <remarks>
/// Three decisions here are load-bearing rather than cosmetic:
///
/// <list type="bullet">
///   <item>
///   <c>Content</c> is <c>text</c> and NOT NULL with a <c>''</c> default. File documents store an empty
///   body rather than a null one so the read path never has to branch on nullability for a column the
///   text path dereferences unconditionally — the same shape <c>projects.instructions</c> uses.
///   </item>
///   <item>
///   The <c>(project_id, title)</c> index is not decorative: the library is always read as one
///   project's set, ordered for the prompt index, and the formatter orders by title. An index that
///   matches that order is what keeps a 50-document library from being a sort on every turn of every
///   job in the project.
///   </item>
///   <item>
///   There is no <c>HasOne(project)</c> relationship. <see cref="ProjectConfiguration"/> ignores its
///   <c>ProjectPersonas</c> and <c>Jobs</c> collections for the same reason: a navigation would make
///   every <c>Project</c> load capable of pulling the library, and the library is the one project child
///   whose rows carry a text body. The documents are loaded explicitly where they are needed.
///   </item>
/// </list>
/// </remarks>
public class ProjectDocumentConfiguration : IEntityTypeConfiguration<ProjectDocument>
{
    public void Configure(EntityTypeBuilder<ProjectDocument> builder)
    {
        builder.ToTable("project_documents");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Id)
            .ValueGeneratedOnAdd()
            .HasDefaultValueSql("gen_random_uuid()");

        builder.Property(d => d.ProjectId)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(d => d.Title)
            .HasMaxLength(255)
            .IsRequired();

        // Stored as the enum's numeric value, matching how ProjectJob.Status and Persona.TrustTier are
        // persisted in this context. The column is narrow because the domain has two members and will
        // not grow past a handful; widening it is a migration, which is the friction we want.
        builder.Property(d => d.Kind)
            .HasConversion<int>()
            .HasDefaultValue(ProjectDocumentKind.Text)
            .IsRequired();

        builder.Property(d => d.Content)
            .HasColumnType("text")
            .HasDefaultValue(string.Empty)
            .IsRequired();

        builder.Property(d => d.FileName)
            .HasMaxLength(255);

        builder.Property(d => d.ContentType)
            .HasMaxLength(128);

        builder.Property(d => d.SizeBytes)
            .HasDefaultValue(0L)
            .IsRequired();

        builder.Property(d => d.S3Key)
            .HasMaxLength(512);

        builder.Property(d => d.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        builder.Property(d => d.UpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP")
            .IsRequired();

        // The library read: WHERE project_id = @x ORDER BY title. Covers the list endpoint, the
        // per-project count that enforces MaxDocumentsPerProject, and the prompt index.
        builder.HasIndex(d => new { d.ProjectId, d.Title })
            .HasDatabaseName("ix_project_documents_project_title");

        // Deliberately no second index. The content endpoint's read is
        // `WHERE id = @docId AND project_id = @pid`: the primary key on id returns exactly one row and
        // project_id is then a filter on that row, so a composite (id, project_id) index would cost a
        // write-time btree maintenance and 50 rows' worth of extra pages to buy nothing. The library
        // read is the only access pattern that needs help, and the composite index above is it.
    }
}
