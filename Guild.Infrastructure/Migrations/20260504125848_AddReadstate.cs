using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReadstate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "read_states",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    member_id = table.Column<string>(type: "text", nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    last_read_message_id = table.Column<string>(type: "text", nullable: false),
                    mention_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_read_states", x => x.id);
                    table.ForeignKey(
                        name: "fk_read_states_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_read_states_guild_members_member_id",
                        column: x => x.member_id,
                        principalTable: "guild_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_read_states_channel_id",
                table: "read_states",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_read_states_member_id",
                table: "read_states",
                column: "member_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "read_states");
        }
    }
}
