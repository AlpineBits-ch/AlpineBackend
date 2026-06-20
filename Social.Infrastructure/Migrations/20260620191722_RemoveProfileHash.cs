using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Social.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveProfileHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_profiles_hash",
                table: "profiles");

            migrationBuilder.DropIndex(
                name: "ix_profiles_user_name",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "hash",
                table: "profiles");

            migrationBuilder.CreateIndex(
                name: "ix_profiles_user_name",
                table: "profiles",
                column: "user_name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_profiles_user_name",
                table: "profiles");

            migrationBuilder.AddColumn<int>(
                name: "hash",
                table: "profiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_profiles_hash",
                table: "profiles",
                column: "hash");

            migrationBuilder.CreateIndex(
                name: "ix_profiles_user_name",
                table: "profiles",
                column: "user_name");
        }
    }
}
