using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "product_catalog_entries",
                columns: table => new
                {
                    barcode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name_de = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    name_fr = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    name_it = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    name_en = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    brand = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: true),
                    quantity_unit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_catalog_entries", x => x.barcode);
                });

            migrationBuilder.CreateTable(
                name: "product_catalog_misses",
                columns: table => new
                {
                    barcode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    first_missed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_attempted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    retry_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_catalog_misses", x => x.barcode);
                });

            migrationBuilder.CreateIndex(
                name: "ix_product_catalog_entries_imported_at",
                table: "product_catalog_entries",
                column: "imported_at");

            migrationBuilder.CreateIndex(
                name: "ix_product_catalog_misses_retry_after",
                table: "product_catalog_misses",
                column: "retry_after",
                filter: "retry_after IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "product_catalog_entries");

            migrationBuilder.DropTable(
                name: "product_catalog_misses");
        }
    }
}
