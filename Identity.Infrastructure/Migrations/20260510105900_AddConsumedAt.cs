using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConsumedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "consumed",
                table: "user_key_packages");

            migrationBuilder.AddColumn<DateTime>(
                name: "consumed_at",
                table: "user_key_packages",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "consumed_at",
                table: "user_key_packages");

            migrationBuilder.AddColumn<bool>(
                name: "consumed",
                table: "user_key_packages",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
