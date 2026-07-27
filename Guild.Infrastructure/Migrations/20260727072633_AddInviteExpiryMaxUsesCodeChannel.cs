using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInviteExpiryMaxUsesCodeChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "channel_id",
                table: "guild_invites",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "code",
                table: "guild_invites",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "expires_at",
                table: "guild_invites",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "max_uses",
                table: "guild_invites",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "use_count",
                table: "guild_invites",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Every pre-existing row got the same "" default for `code` above, which would collide
            // against the unique index created below as soon as a second invite row exists.
            migrationBuilder.Sql(
                "UPDATE guild_invites SET code = upper(substr(md5(random()::text || id), 1, 8)) WHERE code = '';");

            migrationBuilder.CreateIndex(
                name: "ix_guild_invites_channel_id",
                table: "guild_invites",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_guild_invites_code",
                table: "guild_invites",
                column: "code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_guild_invites_channels_channel_id",
                table: "guild_invites",
                column: "channel_id",
                principalTable: "channels",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_guild_invites_channels_channel_id",
                table: "guild_invites");

            migrationBuilder.DropIndex(
                name: "ix_guild_invites_channel_id",
                table: "guild_invites");

            migrationBuilder.DropIndex(
                name: "ix_guild_invites_code",
                table: "guild_invites");

            migrationBuilder.DropColumn(
                name: "channel_id",
                table: "guild_invites");

            migrationBuilder.DropColumn(
                name: "code",
                table: "guild_invites");

            migrationBuilder.DropColumn(
                name: "expires_at",
                table: "guild_invites");

            migrationBuilder.DropColumn(
                name: "max_uses",
                table: "guild_invites");

            migrationBuilder.DropColumn(
                name: "use_count",
                table: "guild_invites");
        }
    }
}
