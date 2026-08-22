using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Discovery.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TrigramSearchTextColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "search_text",
                table: "game_topics",
                type: "text",
                nullable: false,
                defaultValue: "");

            // The trigram index moves onto search_text (name + every alias), the column the search
            // query actually filters on now - it never covered alias text while it sat on name alone.
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_game_topics_name_trgm;");
            migrationBuilder.Sql("CREATE INDEX ix_game_topics_search_text_trgm ON game_topics USING gin (search_text gin_trgm_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_game_topics_search_text_trgm;");
            migrationBuilder.Sql("CREATE INDEX ix_game_topics_name_trgm ON game_topics USING gin (name gin_trgm_ops);");

            migrationBuilder.DropColumn(
                name: "search_text",
                table: "game_topics");
        }
    }
}
