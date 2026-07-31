using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMlsKeyPackageLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "expires_at",
                table: "user_key_packages",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<bool>(
                name: "is_last_resort",
                table: "user_key_packages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Backfill, or every package already on the server lands on 0001-01-01 and reads as
            // expired the moment this deploys - leaving every registered device with zero usable
            // packages and unable to be added to a group until its owner next opens the app.
            migrationBuilder.Sql(
                "UPDATE user_key_packages SET expires_at = created_at + INTERVAL '90 days';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "expires_at",
                table: "user_key_packages");

            migrationBuilder.DropColumn(
                name: "is_last_resort",
                table: "user_key_packages");
        }
    }
}
