using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentMesh.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDefaultContextSizeToPersonas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "default_context_size",
                table: "personas",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "default_context_size",
                table: "personas");
        }
    }
}
