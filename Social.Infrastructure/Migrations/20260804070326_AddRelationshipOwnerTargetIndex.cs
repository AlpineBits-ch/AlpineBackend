using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Social.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRelationshipOwnerTargetIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_relationships_owner_id",
                table: "relationships");

            migrationBuilder.CreateIndex(
                name: "ix_relationships_owner_id_target_id",
                table: "relationships",
                columns: new[] { "owner_id", "target_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_relationships_owner_id_target_id",
                table: "relationships");

            migrationBuilder.CreateIndex(
                name: "ix_relationships_owner_id",
                table: "relationships",
                column: "owner_id");
        }
    }
}
