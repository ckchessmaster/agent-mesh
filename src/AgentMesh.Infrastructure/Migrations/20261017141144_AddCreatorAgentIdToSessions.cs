using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentMesh.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCreatorAgentIdToSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "creator_agent_id",
                table: "agent_sessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_agent_sessions_creator_agent_id",
                table: "agent_sessions",
                column: "creator_agent_id");

            migrationBuilder.AddForeignKey(
                name: "fk_agent_sessions_agents_creator_agent_id",
                table: "agent_sessions",
                column: "creator_agent_id",
                principalTable: "agents",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_agent_sessions_agents_creator_agent_id",
                table: "agent_sessions");

            migrationBuilder.DropIndex(
                name: "ix_agent_sessions_creator_agent_id",
                table: "agent_sessions");

            migrationBuilder.DropColumn(
                name: "creator_agent_id",
                table: "agent_sessions");
        }
    }
}
