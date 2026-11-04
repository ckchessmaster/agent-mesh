using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentMesh.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionPerformanceIndices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_session_messages_session_id_created_at",
                table: "session_messages",
                columns: new[] { "session_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_sessions_driver_id_updated_at",
                table: "agent_sessions",
                columns: new[] { "driver_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_sessions_persona_id_updated_at",
                table: "agent_sessions",
                columns: new[] { "persona_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_sessions_status",
                table: "agent_sessions",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_agent_sessions_updated_at",
                table: "agent_sessions",
                column: "updated_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_session_messages_session_id_created_at",
                table: "session_messages");

            migrationBuilder.DropIndex(
                name: "ix_agent_sessions_driver_id_updated_at",
                table: "agent_sessions");

            migrationBuilder.DropIndex(
                name: "ix_agent_sessions_persona_id_updated_at",
                table: "agent_sessions");

            migrationBuilder.DropIndex(
                name: "ix_agent_sessions_status",
                table: "agent_sessions");

            migrationBuilder.DropIndex(
                name: "ix_agent_sessions_updated_at",
                table: "agent_sessions");
        }
    }
}
