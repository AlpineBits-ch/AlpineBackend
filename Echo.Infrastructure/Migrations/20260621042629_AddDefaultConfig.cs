using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Echo.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDefaultConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "echo_configurations",
                columns: new[] { "id", "created_at", "enforced_singleton", "is_login_enabled", "is_register_enabled", "updated_at" },
                values: new object[] { "ecco_3FQmtSXdg2VUCabuTR1r25imW2m", new DateTimeOffset(new DateTime(2023, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), 1, true, true, new DateTimeOffset(new DateTime(2023, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "echo_configurations",
                keyColumn: "id",
                keyValue: "ecco_3FQmtSXdg2VUCabuTR1r25imW2m");
        }
    }
}
