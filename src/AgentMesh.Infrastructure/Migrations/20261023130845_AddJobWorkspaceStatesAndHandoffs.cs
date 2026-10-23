using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentMesh.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddJobWorkspaceStatesAndHandoffs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "job_handoffs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    from_persona_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    to_persona_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    from_turn_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    to_turn_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    release_reason = table.Column<int>(type: "integer", nullable: true),
                    handoff_note = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    expected_head_commit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    release_deadline_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    declared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_handoffs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "job_workspace_states",
                columns: table => new
                {
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    bundle_s3key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    git_remote_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    git_ref = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    head_commit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_saved_by_persona_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_saved_by_agent_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_saved_turn_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_saved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_holder_agent_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    lease_holder_persona_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    lease_acquired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_workspace_states", x => x.job_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_job_handoffs_status_deadline",
                table: "job_handoffs",
                columns: new[] { "status", "release_deadline_at" });

            migrationBuilder.CreateIndex(
                name: "ux_job_handoffs_open_per_job",
                table: "job_handoffs",
                column: "job_id",
                unique: true,
                filter: "status IN (0, 1, 2)");

            migrationBuilder.CreateIndex(
                name: "ix_job_workspace_states_last_saved_agent",
                table: "job_workspace_states",
                column: "last_saved_by_agent_id");

            migrationBuilder.CreateIndex(
                name: "ix_job_workspace_states_lease_holder",
                table: "job_workspace_states",
                column: "lease_holder_agent_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "job_handoffs");

            migrationBuilder.DropTable(
                name: "job_workspace_states");
        }
    }
}
