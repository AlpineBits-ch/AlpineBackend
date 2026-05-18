using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveCorrelationId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "correlation_id",
                table: "wiki_pages");

            migrationBuilder.DropColumn(
                name: "correlation_id",
                table: "wiki_categories");

            migrationBuilder.DropColumn(
                name: "correlation_id",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "correlation_id",
                table: "guilds");

            migrationBuilder.DropColumn(
                name: "correlation_id",
                table: "channels");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "correlation_id",
                table: "wiki_pages",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "correlation_id",
                table: "wiki_categories",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "correlation_id",
                table: "roles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "correlation_id",
                table: "guilds",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "correlation_id",
                table: "channels",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
