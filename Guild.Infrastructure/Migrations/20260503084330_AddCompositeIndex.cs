using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCompositeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_guild_members_guild_id",
                table: "guild_members");

            migrationBuilder.CreateIndex(
                name: "ix_guild_members_guild_id_user_id",
                table: "guild_members",
                columns: new[] { "guild_id", "user_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_guild_members_guild_id_user_id",
                table: "guild_members");

            migrationBuilder.CreateIndex(
                name: "ix_guild_members_guild_id",
                table: "guild_members",
                column: "guild_id");
        }
    }
}
