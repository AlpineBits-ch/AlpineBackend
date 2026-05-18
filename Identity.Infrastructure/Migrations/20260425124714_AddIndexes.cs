using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_user_keys_application_user_user_id",
                table: "user_keys");

            migrationBuilder.DropForeignKey(
                name: "fk_user_public_keys_application_user_user_id",
                table: "user_public_keys");

            migrationBuilder.CreateIndex(
                name: "ix_asp_net_users_email",
                table: "AspNetUsers",
                column: "email",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_user_keys_users_user_id",
                table: "user_keys",
                column: "user_id",
                principalTable: "AspNetUsers",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_user_public_keys_users_user_id",
                table: "user_public_keys",
                column: "user_id",
                principalTable: "AspNetUsers",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_user_keys_users_user_id",
                table: "user_keys");

            migrationBuilder.DropForeignKey(
                name: "fk_user_public_keys_users_user_id",
                table: "user_public_keys");

            migrationBuilder.DropIndex(
                name: "ix_asp_net_users_email",
                table: "AspNetUsers");

            migrationBuilder.AddForeignKey(
                name: "fk_user_keys_application_user_user_id",
                table: "user_keys",
                column: "user_id",
                principalTable: "AspNetUsers",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_user_public_keys_application_user_user_id",
                table: "user_public_keys",
                column: "user_id",
                principalTable: "AspNetUsers",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
