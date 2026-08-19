using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddThreadStarterMessage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "starter_message_id",
                table: "channels",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_channels_starter_message",
                table: "channels",
                column: "starter_message_id",
                unique: true,
                filter: "starter_message_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_channels_starter_message",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "starter_message_id",
                table: "channels");
        }
    }
}
