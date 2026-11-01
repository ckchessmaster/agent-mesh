using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentMesh.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddJobApprovalPolicyAndHeartbeats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE public.agent_jobs
                    ADD COLUMN IF NOT EXISTS approval_policy VARCHAR(32) NOT NULL DEFAULT 'LeadOrHuman',
                    ADD COLUMN IF NOT EXISTS current_task_id VARCHAR(64) NULL,
                    ADD COLUMN IF NOT EXISTS progress_heartbeat TEXT NULL;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE public.project_jobs
                    ADD COLUMN IF NOT EXISTS approval_policy VARCHAR(32) NOT NULL DEFAULT 'LeadOrHuman',
                    ADD COLUMN IF NOT EXISTS current_task_id VARCHAR(64) NULL,
                    ADD COLUMN IF NOT EXISTS progress_heartbeat TEXT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE public.agent_jobs
                    DROP COLUMN IF EXISTS approval_policy,
                    DROP COLUMN IF EXISTS current_task_id,
                    DROP COLUMN IF EXISTS progress_heartbeat;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE public.project_jobs
                    DROP COLUMN IF EXISTS approval_policy,
                    DROP COLUMN IF EXISTS current_task_id,
                    DROP COLUMN IF EXISTS progress_heartbeat;
                """);
        }
    }
}
