using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Discovery.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TopicSearchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
            migrationBuilder.Sql("CREATE INDEX ix_game_topics_name_trgm ON game_topics USING gin (name gin_trgm_ops);");
            migrationBuilder.Sql("CREATE INDEX ix_tags_display_name_trgm ON tags USING gin (display_name gin_trgm_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_tags_display_name_trgm;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_game_topics_name_trgm;");
        }
    }
}
