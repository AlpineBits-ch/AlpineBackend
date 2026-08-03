using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInboxUnreadAndBroadcastMentions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_read_at",
                table: "read_states",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "message_count_at_read",
                table: "read_states",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "last_message_id",
                table: "channels",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "channel_broadcast_mentions",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    message_id = table.Column<string>(type: "text", nullable: false),
                    message_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    author_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    role_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_channel_broadcast_mentions", x => x.id);
                    table.ForeignKey(
                        name: "fk_channel_broadcast_mentions_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_broadcast_mentions_channel_created",
                table: "channel_broadcast_mentions",
                columns: new[] { "channel_id", "message_created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_broadcast_mentions_created",
                table: "channel_broadcast_mentions",
                column: "message_created_at");

            migrationBuilder.CreateIndex(
                name: "IX_broadcast_mentions_message_role",
                table: "channel_broadcast_mentions",
                columns: new[] { "message_id", "role_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_broadcast_mentions");

            migrationBuilder.DropColumn(
                name: "last_read_at",
                table: "read_states");

            migrationBuilder.DropColumn(
                name: "message_count_at_read",
                table: "read_states");

            migrationBuilder.DropColumn(
                name: "last_message_id",
                table: "channels");
        }
    }
}
