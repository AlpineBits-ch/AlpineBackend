using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bots.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBotCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bot_commands",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    bot_application_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    options_json = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bot_commands", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bot_commands_global_unique",
                table: "bot_commands",
                columns: new[] { "bot_application_id", "name" },
                unique: true,
                filter: "guild_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_bot_commands_guild_unique",
                table: "bot_commands",
                columns: new[] { "bot_application_id", "guild_id", "name" },
                unique: true,
                filter: "guild_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bot_commands");
        }
    }
}
