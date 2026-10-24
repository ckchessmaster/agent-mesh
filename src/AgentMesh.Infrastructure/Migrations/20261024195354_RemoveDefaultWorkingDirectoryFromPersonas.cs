using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentMesh.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveDefaultWorkingDirectoryFromPersonas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "default_working_directory",
                table: "personas");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "default_working_directory",
                table: "personas",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }
    }
}
