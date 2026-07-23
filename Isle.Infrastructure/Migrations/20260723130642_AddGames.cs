using System;
using Isle.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Isle.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:game_mode_state", "cooldown,idle,queuing,resolving,running")
                .Annotation("Npgsql:Enum:game_mode_type", "casual,hardcore")
                .Annotation("Npgsql:Enum:geo_fence_shape", "circle,polygon")
                .Annotation("Npgsql:Enum:rank_requirement", "all_participants,top3,winner")
                .Annotation("Npgsql:Enum:trigger_type", "admin_command,timer,zone_entry")
                .Annotation("Npgsql:PostgresExtension:hstore", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:hstore", ",,");

            migrationBuilder.CreateTable(
                name: "game_mode_definitions",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<GameModeType>(type: "game_mode_type", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    zone_shape = table.Column<GeoFenceShape>(type: "geo_fence_shape", nullable: false),
                    zone_center = table.Column<string>(type: "text", nullable: false),
                    zone_radius = table.Column<float>(type: "real", nullable: false),
                    zone_polygon_points = table.Column<string>(type: "text", nullable: false),
                    trigger_type = table.Column<TriggerType>(type: "trigger_type", nullable: false),
                    trigger_interval = table.Column<TimeSpan>(type: "interval", nullable: true),
                    trigger_min_players_to_trigger = table.Column<int>(type: "integer", nullable: true),
                    max_duration = table.Column<TimeSpan>(type: "interval", nullable: true),
                    cooldown = table.Column<TimeSpan>(type: "interval", nullable: false),
                    min_participants = table.Column<int>(type: "integer", nullable: false),
                    max_participants = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_run_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_game_mode_definitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "game_mode_runs",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    definition_id = table.Column<string>(type: "text", nullable: true),
                    game_mode_type = table.Column<GameModeType>(type: "game_mode_type", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_game_mode_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_game_mode_runs_game_mode_definitions_definition_id",
                        column: x => x.definition_id,
                        principalTable: "game_mode_definitions",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "reward_config",
                columns: table => new
                {
                    game_mode_definition_id = table.Column<string>(type: "text", nullable: false),
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    reward_type = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<int>(type: "integer", nullable: false),
                    cosmetic_id = table.Column<string>(type: "text", nullable: true),
                    applies_to = table.Column<RankRequirement>(type: "rank_requirement", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reward_config", x => new { x.game_mode_definition_id, x.id });
                    table.ForeignKey(
                        name: "fk_reward_config_game_mode_definitions_game_mode_definition_id",
                        column: x => x.game_mode_definition_id,
                        principalTable: "game_mode_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "participant_result",
                columns: table => new
                {
                    game_mode_run_id = table.Column<string>(type: "text", nullable: false),
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    player_id = table.Column<string>(type: "text", nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    rank = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_participant_result", x => new { x.game_mode_run_id, x.id });
                    table.ForeignKey(
                        name: "fk_participant_result_game_mode_runs_game_mode_run_id",
                        column: x => x.game_mode_run_id,
                        principalTable: "game_mode_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_game_mode_runs_definition_id",
                table: "game_mode_runs",
                column: "definition_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "participant_result");

            migrationBuilder.DropTable(
                name: "reward_config");

            migrationBuilder.DropTable(
                name: "game_mode_runs");

            migrationBuilder.DropTable(
                name: "game_mode_definitions");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:hstore", ",,")
                .OldAnnotation("Npgsql:Enum:game_mode_state", "cooldown,idle,queuing,resolving,running")
                .OldAnnotation("Npgsql:Enum:game_mode_type", "casual,hardcore")
                .OldAnnotation("Npgsql:Enum:geo_fence_shape", "circle,polygon")
                .OldAnnotation("Npgsql:Enum:rank_requirement", "all_participants,top3,winner")
                .OldAnnotation("Npgsql:Enum:trigger_type", "admin_command,timer,zone_entry")
                .OldAnnotation("Npgsql:PostgresExtension:hstore", ",,");
        }
    }
}
