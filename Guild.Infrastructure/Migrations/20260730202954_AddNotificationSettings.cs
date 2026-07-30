using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "guild_notification_settings",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    member_id = table.Column<string>(type: "text", nullable: false),
                    level = table.Column<int>(type: "integer", nullable: false),
                    muted_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    suppress_everyone = table.Column<bool>(type: "boolean", nullable: false),
                    suppress_role_mentions = table.Column<bool>(type: "boolean", nullable: false),
                    mobile_push = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guild_notification_settings", x => x.id);
                    table.ForeignKey(
                        name: "fk_guild_notification_settings_guild_members_member_id",
                        column: x => x.member_id,
                        principalTable: "guild_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_overrides",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    member_id = table.Column<string>(type: "text", nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: true),
                    category_id = table.Column<string>(type: "text", nullable: true),
                    level = table.Column<int>(type: "integer", nullable: true),
                    muted_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_overrides", x => x.id);
                    table.ForeignKey(
                        name: "fk_notification_overrides_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_notification_overrides_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_notification_overrides_guild_members_member_id",
                        column: x => x.member_id,
                        principalTable: "guild_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_guild_notification_settings_member_id",
                table: "guild_notification_settings",
                column: "member_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_overrides_category_id",
                table: "notification_overrides",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_overrides_channel_id",
                table: "notification_overrides",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_overrides_member_id_category_id",
                table: "notification_overrides",
                columns: new[] { "member_id", "category_id" },
                unique: true,
                filter: "category_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_notification_overrides_member_id_channel_id",
                table: "notification_overrides",
                columns: new[] { "member_id", "channel_id" },
                unique: true,
                filter: "channel_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "guild_notification_settings");

            migrationBuilder.DropTable(
                name: "notification_overrides");
        }
    }
}
