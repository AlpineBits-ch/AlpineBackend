using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UserNameIndex",
                table: "asp_net_users");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "asp_net_users",
                column: "normalized_user_name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UserNameIndex",
                table: "asp_net_users");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "asp_net_users",
                column: "normalized_user_name",
                unique: true);
        }
    }
}
