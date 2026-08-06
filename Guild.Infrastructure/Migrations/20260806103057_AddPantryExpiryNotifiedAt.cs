using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPantryExpiryNotifiedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "expiry_notified_at",
                table: "pantry_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_pantry_items_expires_at",
                table: "pantry_items",
                column: "expires_at",
                filter: "expiry_notified_at IS NULL AND expires_at IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_pantry_items_expires_at",
                table: "pantry_items");

            migrationBuilder.DropColumn(
                name: "expiry_notified_at",
                table: "pantry_items");
        }
    }
}
