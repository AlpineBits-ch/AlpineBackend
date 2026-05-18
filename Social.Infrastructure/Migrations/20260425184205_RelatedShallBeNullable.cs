using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Social.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RelatedShallBeNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_relationships_related_id",
                table: "relationships");

            migrationBuilder.AlterColumn<string>(
                name: "related_id",
                table: "relationships",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.CreateIndex(
                name: "ix_relationships_related_id",
                table: "relationships",
                column: "related_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_relationships_related_id",
                table: "relationships");

            migrationBuilder.AlterColumn<string>(
                name: "related_id",
                table: "relationships",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_relationships_related_id",
                table: "relationships",
                column: "related_id");
        }
    }
}
