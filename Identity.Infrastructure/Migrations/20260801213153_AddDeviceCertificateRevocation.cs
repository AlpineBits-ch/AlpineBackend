using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceCertificateRevocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "revoked_device_certificates",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    client_device_id = table.Column<string>(type: "text", nullable: false),
                    certificate_fingerprint = table.Column<string>(type: "text", nullable: false),
                    identity_key_version = table.Column<int>(type: "integer", nullable: false),
                    certificate_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_revoked_device_certificates", x => x.id);
                    table.ForeignKey(
                        name: "fk_revoked_device_certificates_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_revoked_device_certificates_user_id_certificate_fingerprint",
                table: "revoked_device_certificates",
                columns: new[] { "user_id", "certificate_fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_revoked_device_certificates_user_id_revoked_at",
                table: "revoked_device_certificates",
                columns: new[] { "user_id", "revoked_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "revoked_device_certificates");
        }
    }
}
