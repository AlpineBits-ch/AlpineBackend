using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Billing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEntitlementVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "entitlement_versions",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    subject_kind = table.Column<string>(type: "text", nullable: false),
                    subject_id = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entitlement_versions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_entitlement_versions_subject_kind_subject_id",
                table: "entitlement_versions",
                columns: new[] { "subject_kind", "subject_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "entitlement_versions");
        }
    }
}
