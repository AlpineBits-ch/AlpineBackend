using Guild.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveMemberType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "type",
                table: "guild_members");

            migrationBuilder.AddColumn<string>(
                name: "bio",
                table: "guild_members",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "nickname",
                table: "guild_members",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bio",
                table: "guild_members");

            migrationBuilder.DropColumn(
                name: "nickname",
                table: "guild_members");

            migrationBuilder.AddColumn<MemberType>(
                name: "type",
                table: "guild_members",
                type: "member_type",
                nullable: false,
                defaultValue: MemberType.Default);
        }
    }
}
