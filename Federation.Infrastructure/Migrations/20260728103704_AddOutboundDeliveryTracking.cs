using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Federation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboundDeliveryTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "attempts",
                table: "federated_events",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "delivered",
                table: "federated_events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "target_host",
                table: "federated_events",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "attempts",
                table: "federated_events");

            migrationBuilder.DropColumn(
                name: "delivered",
                table: "federated_events");

            migrationBuilder.DropColumn(
                name: "target_host",
                table: "federated_events");
        }
    }
}
