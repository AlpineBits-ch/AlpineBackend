using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Import.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "guild_links",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    echo_guild_id = table.Column<string>(type: "text", nullable: false),
                    discord_guild_id = table.Column<string>(type: "text", nullable: false),
                    sync_direction = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    last_synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guild_links", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "import_entity_mappings",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    guild_link_id = table.Column<string>(type: "text", nullable: false),
                    discord_id = table.Column<string>(type: "text", nullable: false),
                    entity_type = table.Column<int>(type: "integer", nullable: false),
                    echo_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_entity_mappings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "import_jobs",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    echo_guild_id = table.Column<string>(type: "text", nullable: true),
                    discord_guild_id = table.Column<string>(type: "text", nullable: false),
                    requested_by_user_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_jobs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_guild_links_discord_guild_id",
                table: "guild_links",
                column: "discord_guild_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_guild_links_echo_guild_id",
                table: "guild_links",
                column: "echo_guild_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_import_entity_mappings_guild_link_id_discord_id_entity_type",
                table: "import_entity_mappings",
                columns: new[] { "guild_link_id", "discord_id", "entity_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_import_entity_mappings_guild_link_id_echo_id_entity_type",
                table: "import_entity_mappings",
                columns: new[] { "guild_link_id", "echo_id", "entity_type" });

            migrationBuilder.CreateIndex(
                name: "ix_import_jobs_discord_guild_id",
                table: "import_jobs",
                column: "discord_guild_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_jobs_requested_by_user_id",
                table: "import_jobs",
                column: "requested_by_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "guild_links");

            migrationBuilder.DropTable(
                name: "import_entity_mappings");

            migrationBuilder.DropTable(
                name: "import_jobs");
        }
    }
}
