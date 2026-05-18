using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_channel_permissions_category_id",
                table: "channel_permissions");

            migrationBuilder.DropIndex(
                name: "ix_channel_permissions_channel_id",
                table: "channel_permissions");

            migrationBuilder.DropIndex(
                name: "ix_channel_permissions_role_id",
                table: "channel_permissions");

            migrationBuilder.CreateIndex(
                name: "IX_channel_permissions_category_id_filtered",
                table: "channel_permissions",
                column: "category_id",
                filter: "category_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_channel_permissions_channel_id_filtered",
                table: "channel_permissions",
                column: "channel_id",
                filter: "channel_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_channel_permissions_role_member",
                table: "channel_permissions",
                columns: new[] { "role_id", "member_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_channel_permissions_category_id_filtered",
                table: "channel_permissions");

            migrationBuilder.DropIndex(
                name: "IX_channel_permissions_channel_id_filtered",
                table: "channel_permissions");

            migrationBuilder.DropIndex(
                name: "IX_channel_permissions_role_member",
                table: "channel_permissions");

            migrationBuilder.CreateIndex(
                name: "ix_channel_permissions_category_id",
                table: "channel_permissions",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_channel_permissions_channel_id",
                table: "channel_permissions",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_channel_permissions_role_id",
                table: "channel_permissions",
                column: "role_id");
        }
    }
}
