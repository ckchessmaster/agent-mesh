using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentMesh.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonasAndDrivers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_agent_jobs_agents_requested_by_agent_id",
                table: "agent_jobs");

            migrationBuilder.AddColumn<string>(
                name: "driver_id",
                table: "agent_sessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "persona_id",
                table: "agent_sessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "personas",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    role = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, defaultValue: "Autonomous Persona"),
                    persona_prompt = table.Column<string>(type: "text", nullable: false),
                    driver_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    default_model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    default_effort = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    default_mode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    default_tier = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    default_working_directory = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    trust_tier = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_personas", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_sessions_driver_id",
                table: "agent_sessions",
                column: "driver_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_sessions_persona_id",
                table: "agent_sessions",
                column: "persona_id");

            migrationBuilder.CreateIndex(
                name: "ix_personas_driver_id",
                table: "personas",
                column: "driver_id");

            migrationBuilder.AddForeignKey(
                name: "fk_agent_jobs_agent_requested_by_agent_id",
                table: "agent_jobs",
                column: "requested_by_agent_id",
                principalTable: "agents",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_agent_jobs_agent_requested_by_agent_id",
                table: "agent_jobs");

            migrationBuilder.DropTable(
                name: "personas");

            migrationBuilder.DropIndex(
                name: "ix_agent_sessions_driver_id",
                table: "agent_sessions");

            migrationBuilder.DropIndex(
                name: "ix_agent_sessions_persona_id",
                table: "agent_sessions");

            migrationBuilder.DropColumn(
                name: "driver_id",
                table: "agent_sessions");

            migrationBuilder.DropColumn(
                name: "persona_id",
                table: "agent_sessions");

            migrationBuilder.AddForeignKey(
                name: "fk_agent_jobs_agents_requested_by_agent_id",
                table: "agent_jobs",
                column: "requested_by_agent_id",
                principalTable: "agents",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
