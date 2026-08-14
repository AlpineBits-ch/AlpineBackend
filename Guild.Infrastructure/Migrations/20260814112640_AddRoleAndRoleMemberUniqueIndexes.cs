using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoleAndRoleMemberUniqueIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_role_members_role_id",
                table: "role_members");

            migrationBuilder.CreateIndex(
                name: "ix_roles_guild_id_everyone",
                table: "roles",
                column: "guild_id",
                unique: true,
                filter: "type = 'everyone'");

            migrationBuilder.CreateIndex(
                name: "ix_role_members_role_id_member_id",
                table: "role_members",
                columns: new[] { "role_id", "member_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_roles_guild_id_everyone",
                table: "roles");

            migrationBuilder.DropIndex(
                name: "ix_role_members_role_id_member_id",
                table: "role_members");

            migrationBuilder.CreateIndex(
                name: "ix_role_members_role_id",
                table: "role_members",
                column: "role_id");
        }
    }
}
