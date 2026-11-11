using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentMesh.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddJobTimelineCardsAndComments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "approval_policy",
                table: "project_jobs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Autonomous",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldDefaultValue: "LeadOrHuman");

            migrationBuilder.AlterColumn<string>(
                name: "approval_policy",
                table: "agent_jobs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Autonomous",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldDefaultValue: "LeadOrHuman");

            migrationBuilder.CreateTable(
                name: "job_timeline_cards",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    task_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    card_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    structured_payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_timeline_cards", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "job_card_comments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sender_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sender_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_card_comments", x => x.id);
                    table.ForeignKey(
                        name: "fk_job_card_comments_job_timeline_cards_card_id",
                        column: x => x.card_id,
                        principalTable: "job_timeline_cards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_job_card_comments_card_id_created_at",
                table: "job_card_comments",
                columns: new[] { "card_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_job_timeline_cards_job_id_created_at",
                table: "job_timeline_cards",
                columns: new[] { "job_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "job_card_comments");

            migrationBuilder.DropTable(
                name: "job_timeline_cards");

            migrationBuilder.AlterColumn<string>(
                name: "approval_policy",
                table: "project_jobs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "LeadOrHuman",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldDefaultValue: "Autonomous");

            migrationBuilder.AlterColumn<string>(
                name: "approval_policy",
                table: "agent_jobs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "LeadOrHuman",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldDefaultValue: "Autonomous");
        }
    }
}
