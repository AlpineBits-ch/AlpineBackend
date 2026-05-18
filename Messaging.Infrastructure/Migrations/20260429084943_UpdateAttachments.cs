using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Messaging.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UpdateAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "thumbnail_id",
                table: "attachments",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "thumbnail_id",
                table: "attachments");
        }
    }
}
