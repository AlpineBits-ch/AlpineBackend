using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Social.Domain.Enums;

#nullable disable

namespace Social.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGameCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:game_catalog_source", "community,manual,seeded")
                .Annotation("Npgsql:Enum:game_platform", "darwin,linux,win32")
                .Annotation("Npgsql:Enum:online_status", "do_not_disturb,hidden,idle,offline,online")
                .Annotation("Npgsql:Enum:profile_font", "default,display,handwritten,monospace,rounded,serif")
                .Annotation("Npgsql:Enum:relationship_status", "blocked,friends,none,pending_incoming,pending_outgoing")
                .OldAnnotation("Npgsql:Enum:online_status", "do_not_disturb,hidden,idle,offline,online")
                .OldAnnotation("Npgsql:Enum:profile_font", "default,display,handwritten,monospace,rounded,serif")
                .OldAnnotation("Npgsql:Enum:relationship_status", "blocked,friends,none,pending_incoming,pending_outgoing");

            migrationBuilder.CreateTable(
                name: "game_applications",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    discord_application_id = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    aliases = table.Column<string[]>(type: "text[]", nullable: false),
                    steam_app_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    source = table.Column<GameCatalogSource>(type: "game_catalog_source", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_game_applications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "game_catalog_states",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    seed_version = table.Column<string>(type: "text", nullable: false),
                    seeded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_game_catalog_states", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "game_executables",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    game_application_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    basename = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    os = table.Column<GamePlatform>(type: "game_platform", nullable: false),
                    is_launcher = table.Column<bool>(type: "boolean", nullable: false),
                    is_negated = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_game_executables", x => x.id);
                    table.ForeignKey(
                        name: "fk_game_executables_game_applications_game_application_id",
                        column: x => x.game_application_id,
                        principalTable: "game_applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_game_applications_discord_application_id",
                table: "game_applications",
                column: "discord_application_id",
                unique: true,
                filter: "discord_application_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_game_applications_name",
                table: "game_applications",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "ix_game_executables_game_application_id",
                table: "game_executables",
                column: "game_application_id");

            migrationBuilder.CreateIndex(
                name: "ix_game_executables_os_basename",
                table: "game_executables",
                columns: new[] { "os", "basename" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "game_catalog_states");

            migrationBuilder.DropTable(
                name: "game_executables");

            migrationBuilder.DropTable(
                name: "game_applications");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:online_status", "do_not_disturb,hidden,idle,offline,online")
                .Annotation("Npgsql:Enum:profile_font", "default,display,handwritten,monospace,rounded,serif")
                .Annotation("Npgsql:Enum:relationship_status", "blocked,friends,none,pending_incoming,pending_outgoing")
                .OldAnnotation("Npgsql:Enum:game_catalog_source", "community,manual,seeded")
                .OldAnnotation("Npgsql:Enum:game_platform", "darwin,linux,win32")
                .OldAnnotation("Npgsql:Enum:online_status", "do_not_disturb,hidden,idle,offline,online")
                .OldAnnotation("Npgsql:Enum:profile_font", "default,display,handwritten,monospace,rounded,serif")
                .OldAnnotation("Npgsql:Enum:relationship_status", "blocked,friends,none,pending_incoming,pending_outgoing");
        }
    }
}
