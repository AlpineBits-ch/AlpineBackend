using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Social.Infrastructure.Migrations
{
    /// <summary>
    /// Adds the <c>resolved</c> member of <c>game_catalog_source</c>, for catalog rows learned on
    /// demand from the application registry rather than seeded from the bootstrap artifact.
    /// </summary>
    public partial class AddResolvedCatalogSource : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TYPE game_catalog_source ADD VALUE IF NOT EXISTS 'resolved' BEFORE 'seeded';",
                suppressTransaction: true);
        }

        /// <summary>Deliberately empty.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
