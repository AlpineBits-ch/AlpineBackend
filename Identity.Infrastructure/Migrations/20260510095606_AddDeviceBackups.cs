using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceBackups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_device_backups",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    device_id = table.Column<string>(type: "text", nullable: false),
                    backup = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_device_backups", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_device_backups_user_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "user_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_device_backups_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_device_backups_device_id",
                table: "user_device_backups",
                column: "device_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_device_backups_user_id",
                table: "user_device_backups",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_device_backups");
        }
    }
}
