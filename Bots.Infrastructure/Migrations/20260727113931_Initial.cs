using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bots.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bot_applications",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    owner_user_id = table.Column<string>(type: "text", nullable: false),
                    bot_user_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    icon_url = table.Column<string>(type: "text", nullable: true),
                    default_permissions = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bot_applications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "bot_installations",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    bot_application_id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    installed_by_user_id = table.Column<string>(type: "text", nullable: false),
                    granted_permissions = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    guild_member_id = table.Column<string>(type: "text", nullable: false),
                    installed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bot_installations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bot_applications_bot_user_id",
                table: "bot_applications",
                column: "bot_user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_bot_applications_owner_user_id",
                table: "bot_applications",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_bot_installations_bot_application_id_guild_id",
                table: "bot_installations",
                columns: new[] { "bot_application_id", "guild_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bot_applications");

            migrationBuilder.DropTable(
                name: "bot_installations");
        }
    }
}
