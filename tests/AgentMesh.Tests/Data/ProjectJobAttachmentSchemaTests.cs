using AgentMesh.Core.Models;
using AgentMesh.Infrastructure.Data.Configurations;
using AgentMesh.Infrastructure.Migrations;
using AgentMesh.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace AgentMesh.Tests.Data;

/// <summary>
/// Pins the <c>project_jobs.attachments</c> JSONB schema, Npgsql column mapping, model snapshot parity,
/// and guarded idempotent DDL in <see cref="AddProjectJobAttachments"/>.
/// </summary>
public class ProjectJobAttachmentSchemaTests
{
    private const string TableName = "project_jobs";
    private const string ColumnName = "attachments";
    private const string MigrationId = "20261122020500_AddProjectJobAttachments";

    private static IEntityType ProjectJobEntity()
    {
        using var db = TestDbContextFactory.Postgres();
        var entity = db.Model.FindEntityType(typeof(ProjectJob));
        entity.Should().NotBeNull($"the model must map {nameof(ProjectJob)}");
        return entity!;
    }

    private static IProperty AttachmentsProperty()
    {
        var entity = ProjectJobEntity();
        var prop = entity.GetProperties().FirstOrDefault(p =>
            string.Equals(p.GetColumnName(), ColumnName, StringComparison.Ordinal));

        prop.Should().NotBeNull($"'{ColumnName}' must be mapped on {TableName}");
        return prop!;
    }

    [Fact]
    public void ProjectJobAttachmentsColumn_IsMappedAsNonNullableJsonbWithEmptyArrayDefault()
    {
        var prop = AttachmentsProperty();

        prop.Name.Should().Be(nameof(ProjectJob.AttachmentsJson));
        prop.GetColumnName().Should().Be(ColumnName);
        prop.GetColumnType().Should().Be("jsonb");
        prop.IsColumnNullable().Should().BeFalse();
        prop.GetDefaultValue().Should().Be("[]");

        var instance = new ProjectJob();
        instance.AttachmentsJson.Should().Be("[]");
    }

    [Fact]
    public void ProjectJobAttachmentsQuery_TranslatesToProjectJobsAttachmentsColumn()
    {
        using var db = TestDbContextFactory.Postgres();
        var sql = db.ProjectJobs.AsNoTracking().Select(j => j.AttachmentsJson).ToQueryString();

        sql.Should().Contain("FROM project_jobs");
        sql.Should().Contain("SELECT p.attachments");
    }

    [Fact]
    public void AddProjectJobAttachmentsMigration_EmitsIdempotentGuardedSqlOnly()
    {
        var upOps = Operations(up: true);
        var downOps = Operations(up: false);

        upOps.Should().OnlyContain(o => o is SqlOperation);
        downOps.Should().OnlyContain(o => o is SqlOperation);

        var upSql = string.Join('\n', upOps.OfType<SqlOperation>().Select(o => o.Sql));
        upSql.Should().Contain("ALTER TABLE project_jobs");
        upSql.Should().Contain("ADD COLUMN IF NOT EXISTS attachments jsonb NOT NULL DEFAULT '[]'::jsonb");

        var downSql = string.Join('\n', downOps.OfType<SqlOperation>().Select(o => o.Sql));
        downSql.Should().Contain("ALTER TABLE project_jobs");
        downSql.Should().Contain("DROP COLUMN IF EXISTS attachments");
    }

    [Fact]
    public void ModelSnapshot_AgreesWithProjectJobConfiguration_AndHasNoPendingChanges()
    {
        using var db = TestDbContextFactory.Postgres();
        var migrations = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        migrations.Last().Should().Be(MigrationId);
        migrations.IndexOf("20261020184553_AddProjectsAndJobs")
            .Should().BeLessThan(migrations.IndexOf(MigrationId));

        db.Database.HasPendingModelChanges().Should().BeFalse(
            "AgentMeshDbContextModelSnapshot must stay in exact sync with ProjectJobConfiguration");

        var mapped = db.Model.FindEntityType(typeof(ProjectJob))!;
        var builder = new ModelBuilder();
        new ProjectJobConfiguration().Configure(builder.Entity<ProjectJob>());
        var expected = builder.Model.FindEntityType(typeof(ProjectJob))!;

        foreach (var expectedProperty in expected.GetProperties())
        {
            var actual = mapped.FindProperty(expectedProperty.Name);
            actual.Should().NotBeNull($"{nameof(ProjectJob)}.{expectedProperty.Name} must be mapped");
            actual!.IsNullable.Should().Be(expectedProperty.IsNullable);
        }
    }

    private static IReadOnlyList<MigrationOperation> Operations(bool up)
    {
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        var migration = new AddProjectJobAttachments();
        var method = typeof(Migration).GetMethod(
            up ? "Up" : "Down",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        method.Invoke(migration, [builder]);
        return builder.Operations;
    }
}
