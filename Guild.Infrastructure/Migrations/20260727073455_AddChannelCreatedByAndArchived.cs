using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelCreatedByAndArchived : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "created_by_user_id",
                table: "channels",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_archived",
                table: "channels",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "created_by_user_id",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "is_archived",
                table: "channels");
        }
    }
}
