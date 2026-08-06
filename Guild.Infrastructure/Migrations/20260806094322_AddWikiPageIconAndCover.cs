using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWikiPageIconAndCover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cover_url",
                table: "wiki_pages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "icon",
                table: "wiki_pages",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cover_url",
                table: "wiki_pages");

            migrationBuilder.DropColumn(
                name: "icon",
                table: "wiki_pages");
        }
    }
}
