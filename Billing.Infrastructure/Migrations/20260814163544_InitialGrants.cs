using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Billing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "grants",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    subject_kind = table.Column<string>(type: "text", nullable: false),
                    subject_id = table.Column<string>(type: "text", nullable: false),
                    grant_kind = table.Column<string>(type: "text", nullable: false),
                    plan = table.Column<string>(type: "text", nullable: true),
                    entitlements_json = table.Column<string>(type: "text", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by = table.Column<string>(type: "text", nullable: true),
                    revoke_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_grants", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_grants_expires_at",
                table: "grants",
                column: "expires_at",
                filter: "expires_at IS NOT NULL AND revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_grants_subject_kind_subject_id",
                table: "grants",
                columns: new[] { "subject_kind", "subject_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "grants");
        }
    }
}
