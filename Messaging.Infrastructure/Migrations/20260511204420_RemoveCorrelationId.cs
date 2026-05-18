using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Messaging.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveCorrelationId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "correlation_id",
                table: "conversations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "correlation_id",
                table: "conversations",
                type: "text",
                nullable: false,
                defaultValue: "");
        }
    }
}
