using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Messaging.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLastReadMessageId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "last_read_message_id",
                table: "members",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "mention_count",
                table: "members",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_read_message_id",
                table: "members");

            migrationBuilder.DropColumn(
                name: "mention_count",
                table: "members");
        }
    }
}
