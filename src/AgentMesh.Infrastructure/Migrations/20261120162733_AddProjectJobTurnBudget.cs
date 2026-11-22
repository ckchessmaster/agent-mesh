using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentMesh.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Rewritten as guarded idempotent DDL (the repo's migration convention, e.g.
    /// <c>AddMcpServersAndPersonaMcpServers</c>) so startup <c>MigrateAsync()</c> and manual reruns
    /// remain safe against a partially migrated database.
    /// </remarks>
    public partial class AddProjectJobTurnBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE project_jobs
                    ADD COLUMN IF NOT EXISTS turn_budget integer NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE project_jobs
                    DROP COLUMN IF EXISTS turn_budget;
                """);
        }
    }
}
