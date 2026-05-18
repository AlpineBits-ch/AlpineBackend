using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Social.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRelatedId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "correlation_id",
                table: "relationships",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "related_id",
                table: "relationships",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ix_relationships_related_id",
                table: "relationships",
                column: "related_id");

            migrationBuilder.AddForeignKey(
                name: "fk_relationships_relationships_related_id",
                table: "relationships",
                column: "related_id",
                principalTable: "relationships",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_relationships_relationships_related_id",
                table: "relationships");

            migrationBuilder.DropIndex(
                name: "ix_relationships_related_id",
                table: "relationships");

            migrationBuilder.DropColumn(
                name: "correlation_id",
                table: "relationships");

            migrationBuilder.DropColumn(
                name: "related_id",
                table: "relationships");
        }
    }
}
