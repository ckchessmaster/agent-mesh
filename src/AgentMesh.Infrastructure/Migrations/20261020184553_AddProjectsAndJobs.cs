using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentMesh.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectsAndJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "job_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "user"),
                    sender_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    sender_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    content = table.Column<string>(type: "text", nullable: false),
                    mentions = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    turn_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    structured_payload = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "project_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false, defaultValue: ""),
                    status = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    git_branch_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, defaultValue: ""),
                    s3archive_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    tasks = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    result_summary = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "project_personas",
                columns: table => new
                {
                    project_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    persona_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_personas", x => new { x.project_id, x.persona_id });
                    table.ForeignKey(
                        name: "fk_project_personas_personas_persona_id",
                        column: x => x.persona_id,
                        principalTable: "personas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false, defaultValue: ""),
                    workspace_type = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    git_repository_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    git_base_branch = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, defaultValue: "main"),
                    s3bucket_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    s3prefix_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    project_instructions = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    default_lead_persona_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_projects", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_job_messages_job_id",
                table: "job_messages",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_job_messages_turn_session_id",
                table: "job_messages",
                column: "turn_session_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_jobs_project_id",
                table: "project_jobs",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_jobs_status",
                table: "project_jobs",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_project_personas_persona_id",
                table: "project_personas",
                column: "persona_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_personas_project_id",
                table: "project_personas",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_projects_default_lead_persona_id",
                table: "projects",
                column: "default_lead_persona_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "job_messages");

            migrationBuilder.DropTable(
                name: "project_jobs");

            migrationBuilder.DropTable(
                name: "project_personas");

            migrationBuilder.DropTable(
                name: "projects");
        }
    }
}
