using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Echo.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "echo_configurations",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    is_register_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    is_login_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    enforced_singleton = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_echo_configurations", x => x.id);
                    table.CheckConstraint("ck_single_row_enforcer", "[enforced_singleton] = 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_echo_configurations_enforced_singleton",
                table: "echo_configurations",
                column: "enforced_singleton",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "echo_configurations");
        }
    }
}
