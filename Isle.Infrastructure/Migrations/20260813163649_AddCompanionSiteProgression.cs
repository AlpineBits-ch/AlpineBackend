using System;
using Isle.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isle.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanionSiteProgression : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:game_mode_state", "cooldown,idle,queuing,resolving,running")
                .Annotation("Npgsql:Enum:game_mode_type", "casual,hardcore")
                .Annotation("Npgsql:Enum:geo_fence_shape", "circle,polygon")
                .Annotation("Npgsql:Enum:play_session_end_reason", "abandoned,disconnected,left,species_change")
                .Annotation("Npgsql:Enum:quest_instance_state", "active,cancelled,completed,expired")
                .Annotation("Npgsql:Enum:quest_type", "bounty,exploration,hunt")
                .Annotation("Npgsql:Enum:rank_requirement", "all_participants,top3,winner")
                .Annotation("Npgsql:Enum:reward_type", "cosmetic_unlock,full_diet,full_health,full_stamina,full_water,growth_boost,half_diet,half_water,storage_slot,xp")
                .Annotation("Npgsql:Enum:trigger_type", "admin_command,timer,zone_entry")
                .Annotation("Npgsql:PostgresExtension:hstore", ",,")
                .OldAnnotation("Npgsql:Enum:game_mode_state", "cooldown,idle,queuing,resolving,running")
                .OldAnnotation("Npgsql:Enum:game_mode_type", "casual,hardcore")
                .OldAnnotation("Npgsql:Enum:geo_fence_shape", "circle,polygon")
                .OldAnnotation("Npgsql:Enum:quest_instance_state", "active,cancelled,completed,expired")
                .OldAnnotation("Npgsql:Enum:quest_type", "bounty,exploration,hunt")
                .OldAnnotation("Npgsql:Enum:rank_requirement", "all_participants,top3,winner")
                .OldAnnotation("Npgsql:Enum:reward_type", "cosmetic_unlock,full_diet,full_health,full_stamina,full_water,growth_boost,half_diet,half_water,storage_slot,xp")
                .OldAnnotation("Npgsql:Enum:trigger_type", "admin_command,timer,zone_entry")
                .OldAnnotation("Npgsql:PostgresExtension:hstore", ",,");

            migrationBuilder.AddColumn<bool>(
                name: "is_equipped",
                table: "skins",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "name",
                table: "skins",
                type: "character varying(48)",
                maxLength: 48,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "play_sessions",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    player_id = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    duration_seconds = table.Column<long>(type: "bigint", nullable: false),
                    species = table.Column<string>(type: "text", nullable: true),
                    end_reason = table.Column<PlaySessionEndReason>(type: "play_session_end_reason", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_play_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_play_sessions_players_player_id",
                        column: x => x.player_id,
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "player_preferences",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    player_id = table.Column<string>(type: "text", nullable: false),
                    notify_server_status = table.Column<bool>(type: "boolean", nullable: false),
                    notify_quest_complete = table.Column<bool>(type: "boolean", nullable: false),
                    notify_dino_death = table.Column<bool>(type: "boolean", nullable: false),
                    show_on_leaderboard = table.Column<bool>(type: "boolean", nullable: false),
                    public_profile = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_player_preferences", x => x.id);
                    table.ForeignKey(
                        name: "fk_player_preferences_players_player_id",
                        column: x => x.player_id,
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "quest_participations",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    quest_instance_id = table.Column<string>(type: "text", nullable: false),
                    player_id = table.Column<string>(type: "text", nullable: false),
                    progress = table.Column<int>(type: "integer", nullable: false),
                    goal = table.Column<int>(type: "integer", nullable: false),
                    rank = table.Column<RankRequirement>(type: "rank_requirement", nullable: true),
                    was_paid = table.Column<bool>(type: "boolean", nullable: false),
                    reward_summary = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<QuestInstanceState>(type: "quest_instance_state", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quest_participations", x => x.id);
                    table.ForeignKey(
                        name: "fk_quest_participations_players_player_id",
                        column: x => x.player_id,
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_quest_participations_quest_instances_quest_instance_id",
                        column: x => x.quest_instance_id,
                        principalTable: "quest_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_play_sessions_ended_at",
                table: "play_sessions",
                column: "ended_at");

            migrationBuilder.CreateIndex(
                name: "ix_play_sessions_player_id_ended_at",
                table: "play_sessions",
                columns: new[] { "player_id", "ended_at" });

            migrationBuilder.CreateIndex(
                name: "ix_play_sessions_player_id_species",
                table: "play_sessions",
                columns: new[] { "player_id", "species" });

            migrationBuilder.CreateIndex(
                name: "ix_player_preferences_player_id",
                table: "player_preferences",
                column: "player_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_quest_participations_player_id_recorded_at",
                table: "quest_participations",
                columns: new[] { "player_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "ix_quest_participations_quest_instance_id_player_id",
                table: "quest_participations",
                columns: new[] { "quest_instance_id", "player_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "play_sessions");

            migrationBuilder.DropTable(
                name: "player_preferences");

            migrationBuilder.DropTable(
                name: "quest_participations");

            migrationBuilder.DropColumn(
                name: "is_equipped",
                table: "skins");

            migrationBuilder.DropColumn(
                name: "name",
                table: "skins");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:game_mode_state", "cooldown,idle,queuing,resolving,running")
                .Annotation("Npgsql:Enum:game_mode_type", "casual,hardcore")
                .Annotation("Npgsql:Enum:geo_fence_shape", "circle,polygon")
                .Annotation("Npgsql:Enum:quest_instance_state", "active,cancelled,completed,expired")
                .Annotation("Npgsql:Enum:quest_type", "bounty,exploration,hunt")
                .Annotation("Npgsql:Enum:rank_requirement", "all_participants,top3,winner")
                .Annotation("Npgsql:Enum:reward_type", "cosmetic_unlock,full_diet,full_health,full_stamina,full_water,growth_boost,half_diet,half_water,storage_slot,xp")
                .Annotation("Npgsql:Enum:trigger_type", "admin_command,timer,zone_entry")
                .Annotation("Npgsql:PostgresExtension:hstore", ",,")
                .OldAnnotation("Npgsql:Enum:game_mode_state", "cooldown,idle,queuing,resolving,running")
                .OldAnnotation("Npgsql:Enum:game_mode_type", "casual,hardcore")
                .OldAnnotation("Npgsql:Enum:geo_fence_shape", "circle,polygon")
                .OldAnnotation("Npgsql:Enum:play_session_end_reason", "abandoned,disconnected,left,species_change")
                .OldAnnotation("Npgsql:Enum:quest_instance_state", "active,cancelled,completed,expired")
                .OldAnnotation("Npgsql:Enum:quest_type", "bounty,exploration,hunt")
                .OldAnnotation("Npgsql:Enum:rank_requirement", "all_participants,top3,winner")
                .OldAnnotation("Npgsql:Enum:reward_type", "cosmetic_unlock,full_diet,full_health,full_stamina,full_water,growth_boost,half_diet,half_water,storage_slot,xp")
                .OldAnnotation("Npgsql:Enum:trigger_type", "admin_command,timer,zone_entry")
                .OldAnnotation("Npgsql:PostgresExtension:hstore", ",,");
        }
    }
}
