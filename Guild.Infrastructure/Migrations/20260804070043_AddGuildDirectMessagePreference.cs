using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGuildDirectMessagePreference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "guild_direct_message_preferences",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    allow_direct_messages = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guild_direct_message_preferences", x => x.id);
                    table.ForeignKey(
                        name: "fk_guild_direct_message_preferences_guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_guild_direct_message_preferences_guild_id",
                table: "guild_direct_message_preferences",
                column: "guild_id");

            migrationBuilder.CreateIndex(
                name: "ix_guild_direct_message_preferences_user_id",
                table: "guild_direct_message_preferences",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_guild_direct_message_preferences_user_id_guild_id",
                table: "guild_direct_message_preferences",
                columns: new[] { "user_id", "guild_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "guild_direct_message_preferences");
        }
    }
}
