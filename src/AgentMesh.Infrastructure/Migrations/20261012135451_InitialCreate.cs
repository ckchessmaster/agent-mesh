using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentMesh.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "active"),
                    metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    runtime_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    trust_tier = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    persona_prompt = table.Column<string>(type: "text", nullable: false),
                    memory_summary = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    api_key_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "agent_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by_agent_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    target_service = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    action_verb = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    approval_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    execution_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    temporal_workflow_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_jobs", x => x.id);
                    table.ForeignKey(
                        name: "fk_agent_jobs_agent_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "agent_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_agent_jobs_agents_requested_by_agent_id",
                        column: x => x.requested_by_agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "session_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_agent_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    target_agent_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    content = table.Column<string>(type: "text", nullable: false),
                    structured_payload = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_messages", x => x.id);
                    table.ForeignKey(
                        name: "fk_session_messages_agent_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "agent_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_session_messages_agents_sender_agent_id",
                        column: x => x.sender_agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_session_messages_agents_target_agent_id",
                        column: x => x.target_agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "idx_jobs_session",
                table: "agent_jobs",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "idx_jobs_status",
                table: "agent_jobs",
                column: "execution_status");

            migrationBuilder.CreateIndex(
                name: "ix_agent_jobs_requested_by_agent_id",
                table: "agent_jobs",
                column: "requested_by_agent_id");

            migrationBuilder.CreateIndex(
                name: "idx_session_messages_session",
                table: "session_messages",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_session_messages_sender_agent_id",
                table: "session_messages",
                column: "sender_agent_id");

            migrationBuilder.CreateIndex(
                name: "ix_session_messages_target_agent_id",
                table: "session_messages",
                column: "target_agent_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_jobs");

            migrationBuilder.DropTable(
                name: "session_messages");

            migrationBuilder.DropTable(
                name: "agent_sessions");

            migrationBuilder.DropTable(
                name: "agents");
        }
    }
}
