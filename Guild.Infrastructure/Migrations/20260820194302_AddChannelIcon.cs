using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelIcon : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "icon",
                table: "channels",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "icon_color",
                table: "channels",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "icon",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "icon_color",
                table: "channels");
        }
    }
}
