using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentMesh.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WidenJobPromptsAndDropJobMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS public.job_messages CASCADE;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE public.project_jobs
                    ALTER COLUMN description TYPE text;
                """);

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'agent_jobs'
                          AND column_name = 'initial_prompt'
                    ) THEN
                        ALTER TABLE public.agent_jobs ADD COLUMN initial_prompt text;
                    ELSE
                        ALTER TABLE public.agent_jobs ALTER COLUMN initial_prompt TYPE text;
                    END IF;

                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'agent_jobs'
                          AND column_name = 'message_text'
                    ) THEN
                        ALTER TABLE public.agent_jobs ADD COLUMN message_text text;
                    ELSE
                        ALTER TABLE public.agent_jobs ALTER COLUMN message_text TYPE text;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "initial_prompt",
                table: "agent_jobs");

            migrationBuilder.DropColumn(
                name: "message_text",
                table: "agent_jobs");

            migrationBuilder.AlterColumn<string>(
                name: "description",
                table: "project_jobs",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "");

            migrationBuilder.CreateTable(
                name: "job_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mentions = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    sender_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    sender_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    sender_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "user"),
                    structured_payload = table.Column<string>(type: "jsonb", nullable: true),
                    turn_session_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_messages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_job_messages_job_id",
                table: "job_messages",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_job_messages_turn_session_id",
                table: "job_messages",
                column: "turn_session_id");
        }
    }
}
