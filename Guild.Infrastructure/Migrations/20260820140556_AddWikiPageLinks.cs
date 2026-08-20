using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <summary>
    /// The page graph. No foreign key on target_page_id: a link to a page nobody has written yet is
    /// a red link, and an FK would refuse it.
    /// </summary>
    public partial class AddWikiPageLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wiki_page_links",
                columns: table => new
                {
                    source_page_id = table.Column<string>(type: "text", nullable: false),
                    target_page_id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    heading_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wiki_page_links", x => new { x.source_page_id, x.target_page_id });
                    table.ForeignKey(
                        name: "fk_wiki_page_links_wiki_pages_source_page_id",
                        column: x => x.source_page_id,
                        principalTable: "wiki_pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_wiki_page_links_guild_id_target_page_id",
                table: "wiki_page_links",
                columns: new[] { "guild_id", "target_page_id" });

            // Everything written before the table existed.
            migrationBuilder.Sql(WikiPageLinkBackfill.ExtractExistingLinksSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wiki_page_links");
        }
    }
}
