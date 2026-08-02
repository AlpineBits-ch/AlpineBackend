using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMemberInviteCodeAndSetNullInviteFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_guild_members_guild_invites_invite_id",
                table: "guild_members");

            migrationBuilder.AddColumn<string>(
                name: "invite_code",
                table: "guild_members",
                type: "text",
                nullable: true);

            // Backfill from the invites that still exist, so attribution isn't only forward-looking.
            // Members whose invite was already deleted are unrecoverable - the old ON DELETE CASCADE
            // took the member rows with it, so there is nothing left to backfill from.
            migrationBuilder.Sql(@"
                UPDATE guild_members m
                SET invite_code = i.code
                FROM guild_invites i
                WHERE m.invite_id = i.id AND m.invite_code IS NULL;");

            migrationBuilder.AddForeignKey(
                name: "fk_guild_members_guild_invites_invite_id",
                table: "guild_members",
                column: "invite_id",
                principalTable: "guild_invites",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_guild_members_guild_invites_invite_id",
                table: "guild_members");

            migrationBuilder.DropColumn(
                name: "invite_code",
                table: "guild_members");

            migrationBuilder.AddForeignKey(
                name: "fk_guild_members_guild_invites_invite_id",
                table: "guild_members",
                column: "invite_id",
                principalTable: "guild_invites",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
