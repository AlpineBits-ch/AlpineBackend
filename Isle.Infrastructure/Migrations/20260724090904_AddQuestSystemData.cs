using System;
using Isle.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Isle.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestSystemData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reward_config");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:game_mode_state", "cooldown,idle,queuing,resolving,running")
                .Annotation("Npgsql:Enum:game_mode_type", "casual,hardcore")
                .Annotation("Npgsql:Enum:geo_fence_shape", "circle,polygon")
                .Annotation("Npgsql:Enum:rank_requirement", "all_participants,top3,winner")
                .Annotation("Npgsql:Enum:reward_type", "cosmetic_unlock,xp")
                .Annotation("Npgsql:Enum:trigger_type", "admin_command,timer,zone_entry")
                .Annotation("Npgsql:PostgresExtension:hstore", ",,")
                .OldAnnotation("Npgsql:Enum:game_mode_state", "cooldown,idle,queuing,resolving,running")
                .OldAnnotation("Npgsql:Enum:game_mode_type", "casual,hardcore")
                .OldAnnotation("Npgsql:Enum:geo_fence_shape", "circle,polygon")
                .OldAnnotation("Npgsql:Enum:rank_requirement", "all_participants,top3,winner")
                .OldAnnotation("Npgsql:Enum:trigger_type", "admin_command,timer,zone_entry")
                .OldAnnotation("Npgsql:PostgresExtension:hstore", ",,");

            migrationBuilder.CreateTable(
                name: "game_mode_definitions_rewards",
                columns: table => new
                {
                    game_mode_definition_id = table.Column<string>(type: "text", nullable: false),
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    reward_type = table.Column<RewardType>(type: "reward_type", nullable: false),
                    amount = table.Column<int>(type: "integer", nullable: false),
                    cosmetic_id = table.Column<string>(type: "text", nullable: true),
                    applies_to = table.Column<RankRequirement>(type: "rank_requirement", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_game_mode_definitions_rewards", x => new { x.game_mode_definition_id, x.id });
                    table.ForeignKey(
                        name: "fk_game_mode_definitions_rewards_game_mode_definitions_game_mo",
                        column: x => x.game_mode_definition_id,
                        principalTable: "game_mode_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "quest",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quest", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "quest_location",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    geo_fence_shape = table.Column<GeoFenceShape>(type: "geo_fence_shape", nullable: false),
                    geo_fence_center = table.Column<string>(type: "text", nullable: false),
                    geo_fence_radius = table.Column<float>(type: "real", nullable: false),
                    geo_fence_polygon_points = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quest_location", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "quest_rewards",
                columns: table => new
                {
                    quest_id = table.Column<string>(type: "text", nullable: false),
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    reward_type = table.Column<RewardType>(type: "reward_type", nullable: false),
                    amount = table.Column<int>(type: "integer", nullable: false),
                    cosmetic_id = table.Column<string>(type: "text", nullable: true),
                    applies_to = table.Column<RankRequirement>(type: "rank_requirement", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quest_rewards", x => new { x.quest_id, x.id });
                    table.ForeignKey(
                        name: "fk_quest_rewards_quest_quest_id",
                        column: x => x.quest_id,
                        principalTable: "quest",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "quest_quest_location",
                columns: table => new
                {
                    locations_id = table.Column<string>(type: "text", nullable: false),
                    quests_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quest_quest_location", x => new { x.locations_id, x.quests_id });
                    table.ForeignKey(
                        name: "fk_quest_quest_location_quest_location_locations_id",
                        column: x => x.locations_id,
                        principalTable: "quest_location",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_quest_quest_location_quest_quests_id",
                        column: x => x.quests_id,
                        principalTable: "quest",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_quest_quest_location_quests_id",
                table: "quest_quest_location",
                column: "quests_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "game_mode_definitions_rewards");

            migrationBuilder.DropTable(
                name: "quest_quest_location");

            migrationBuilder.DropTable(
                name: "quest_rewards");

            migrationBuilder.DropTable(
                name: "quest_location");

            migrationBuilder.DropTable(
                name: "quest");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:game_mode_state", "cooldown,idle,queuing,resolving,running")
                .Annotation("Npgsql:Enum:game_mode_type", "casual,hardcore")
                .Annotation("Npgsql:Enum:geo_fence_shape", "circle,polygon")
                .Annotation("Npgsql:Enum:rank_requirement", "all_participants,top3,winner")
                .Annotation("Npgsql:Enum:trigger_type", "admin_command,timer,zone_entry")
                .Annotation("Npgsql:PostgresExtension:hstore", ",,")
                .OldAnnotation("Npgsql:Enum:game_mode_state", "cooldown,idle,queuing,resolving,running")
                .OldAnnotation("Npgsql:Enum:game_mode_type", "casual,hardcore")
                .OldAnnotation("Npgsql:Enum:geo_fence_shape", "circle,polygon")
                .OldAnnotation("Npgsql:Enum:rank_requirement", "all_participants,top3,winner")
                .OldAnnotation("Npgsql:Enum:reward_type", "cosmetic_unlock,xp")
                .OldAnnotation("Npgsql:Enum:trigger_type", "admin_command,timer,zone_entry")
                .OldAnnotation("Npgsql:PostgresExtension:hstore", ",,");

            migrationBuilder.CreateTable(
                name: "reward_config",
                columns: table => new
                {
                    game_mode_definition_id = table.Column<string>(type: "text", nullable: false),
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    amount = table.Column<int>(type: "integer", nullable: false),
                    applies_to = table.Column<RankRequirement>(type: "rank_requirement", nullable: false),
                    cosmetic_id = table.Column<string>(type: "text", nullable: true),
                    reward_type = table.Column<string>(type: "text", nullable: false)
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
        }
    }
}
