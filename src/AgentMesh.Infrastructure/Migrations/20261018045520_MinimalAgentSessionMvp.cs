using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentMesh.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MinimalAgentSessionMvp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_agent_sessions_agents_creator_agent_id",
                table: "agent_sessions");

            migrationBuilder.DropForeignKey(
                name: "fk_session_messages_agents_sender_agent_id",
                table: "session_messages");

            migrationBuilder.DropForeignKey(
                name: "fk_session_messages_agents_target_agent_id",
                table: "session_messages");

            migrationBuilder.DropIndex(
                name: "ix_session_messages_sender_agent_id",
                table: "session_messages");

            migrationBuilder.DropIndex(
                name: "ix_session_messages_target_agent_id",
                table: "session_messages");

            migrationBuilder.AddColumn<string>(
                name: "event_type",
                table: "session_messages",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "response");

            migrationBuilder.AddColumn<string>(
                name: "tool_args",
                table: "session_messages",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tool_name",
                table: "session_messages",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tool_result",
                table: "session_messages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "capabilities",
                table: "agents",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_heartbeat_at",
                table: "agents",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP");

            migrationBuilder.AddColumn<string>(
                name: "role",
                table: "agents",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "Autonomous Agent");

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "agents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "online");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "event_type",
                table: "session_messages");

            migrationBuilder.DropColumn(
                name: "tool_args",
                table: "session_messages");

            migrationBuilder.DropColumn(
                name: "tool_name",
                table: "session_messages");

            migrationBuilder.DropColumn(
                name: "tool_result",
                table: "session_messages");

            migrationBuilder.DropColumn(
                name: "capabilities",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "last_heartbeat_at",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "role",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "status",
                table: "agents");

            migrationBuilder.CreateIndex(
                name: "ix_session_messages_sender_agent_id",
                table: "session_messages",
                column: "sender_agent_id");

            migrationBuilder.CreateIndex(
                name: "ix_session_messages_target_agent_id",
                table: "session_messages",
                column: "target_agent_id");

            migrationBuilder.AddForeignKey(
                name: "fk_agent_sessions_agents_creator_agent_id",
                table: "agent_sessions",
                column: "creator_agent_id",
                principalTable: "agents",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_session_messages_agents_sender_agent_id",
                table: "session_messages",
                column: "sender_agent_id",
                principalTable: "agents",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_session_messages_agents_target_agent_id",
                table: "session_messages",
                column: "target_agent_id",
                principalTable: "agents",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
