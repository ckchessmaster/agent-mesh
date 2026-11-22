using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentMesh.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Hand-written as guarded idempotent DDL so startup <c>MigrateAsync()</c> and manual reruns
    /// remain safe against a partially migrated database, matching <see cref="AddProjectJobArtifacts"/>.
    /// </remarks>
    public partial class AddProjectJobAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE project_jobs
                    ADD COLUMN IF NOT EXISTS attachments jsonb NOT NULL DEFAULT '[]'::jsonb;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE project_jobs
                    DROP COLUMN IF EXISTS attachments;
                """);
        }
    }
}
