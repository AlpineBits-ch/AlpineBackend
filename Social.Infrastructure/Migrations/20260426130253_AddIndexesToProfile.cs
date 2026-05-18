using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Social.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIndexesToProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_profiles_hash",
                table: "profiles",
                column: "hash");

            migrationBuilder.CreateIndex(
                name: "ix_profiles_user_id",
                table: "profiles",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_profiles_user_name",
                table: "profiles",
                column: "user_name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_profiles_hash",
                table: "profiles");

            migrationBuilder.DropIndex(
                name: "ix_profiles_user_id",
                table: "profiles");

            migrationBuilder.DropIndex(
                name: "ix_profiles_user_name",
                table: "profiles");
        }
    }
}
