using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentMesh.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddS3ArtifactsToAgentJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "artifacts",
                table: "agent_jobs",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "payload_s3_uri",
                table: "agent_jobs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "result_s3_uri",
                table: "agent_jobs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "artifacts",
                table: "agent_jobs");

            migrationBuilder.DropColumn(
                name: "payload_s3_uri",
                table: "agent_jobs");

            migrationBuilder.DropColumn(
                name: "result_s3_uri",
                table: "agent_jobs");
        }
    }
}
