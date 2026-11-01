using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentMesh.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddJobFirstLifecycleSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "approval_policy",
                table: "project_jobs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "LeadOrHuman");

            migrationBuilder.AddColumn<string>(
                name: "assigned_agent_id",
                table: "project_jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "current_task_id",
                table: "project_jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "progress_heartbeat",
                table: "project_jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "working_directory",
                table: "project_jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "approval_policy",
                table: "agent_jobs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "LeadOrHuman");

            migrationBuilder.AddColumn<string>(
                name: "assigned_agent_id",
                table: "agent_jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "current_task_id",
                table: "agent_jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "progress_heartbeat",
                table: "agent_jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "project_id",
                table: "agent_jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "target_branch",
                table: "agent_jobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "working_directory",
                table: "agent_jobs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "approval_policy",
                table: "project_jobs");

            migrationBuilder.DropColumn(
                name: "assigned_agent_id",
                table: "project_jobs");

            migrationBuilder.DropColumn(
                name: "current_task_id",
                table: "project_jobs");

            migrationBuilder.DropColumn(
                name: "progress_heartbeat",
                table: "project_jobs");

            migrationBuilder.DropColumn(
                name: "working_directory",
                table: "project_jobs");

            migrationBuilder.DropColumn(
                name: "approval_policy",
                table: "agent_jobs");

            migrationBuilder.DropColumn(
                name: "assigned_agent_id",
                table: "agent_jobs");

            migrationBuilder.DropColumn(
                name: "current_task_id",
                table: "agent_jobs");

            migrationBuilder.DropColumn(
                name: "progress_heartbeat",
                table: "agent_jobs");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "agent_jobs");

            migrationBuilder.DropColumn(
                name: "target_branch",
                table: "agent_jobs");

            migrationBuilder.DropColumn(
                name: "working_directory",
                table: "agent_jobs");
        }
    }
}
