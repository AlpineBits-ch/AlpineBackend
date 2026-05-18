using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixDeviceTOken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_user_device_tokens_asp_net_users_user_id1",
                table: "user_device_tokens");

            migrationBuilder.DropIndex(
                name: "ix_user_device_tokens_user_id1",
                table: "user_device_tokens");

            migrationBuilder.DropColumn(
                name: "user_id1",
                table: "user_device_tokens");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "user_id1",
                table: "user_device_tokens",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ix_user_device_tokens_user_id1",
                table: "user_device_tokens",
                column: "user_id1");

            migrationBuilder.AddForeignKey(
                name: "fk_user_device_tokens_asp_net_users_user_id1",
                table: "user_device_tokens",
                column: "user_id1",
                principalTable: "asp_net_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
