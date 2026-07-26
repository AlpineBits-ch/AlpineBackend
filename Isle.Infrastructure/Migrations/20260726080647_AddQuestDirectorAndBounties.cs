using System;
using Isle.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Isle.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddQuestDirectorAndBounties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_quest_quest_location_quest_location_locations_id",
                table: "quest_quest_location");

            migrationBuilder.DropForeignKey(
                name: "fk_quest_quest_location_quest_quests_id",
                table: "quest_quest_location");

            // Hand-edited: EF scaffolded the owned rewards table as a drop-and-recreate purely because
            // its parent renamed quest -> quests. The column set is identical, so this is a rename
            // instead — a DROP TABLE here would silently discard any authored quest rewards.
            migrationBuilder.DropForeignKey(
                name: "fk_quest_rewards_quest_quest_id",
                table: "quest_rewards");

            migrationBuilder.DropPrimaryKey(
                name: "pk_quest_rewards",
                table: "quest_rewards");

            migrationBuilder.DropPrimaryKey(
                name: "pk_quest_location",
                table: "quest_location");

            migrationBuilder.DropPrimaryKey(
                name: "pk_quest",
                table: "quest");

            migrationBuilder.RenameTable(
                name: "quest_rewards",
                newName: "quests_rewards");

            migrationBuilder.RenameTable(
                name: "quest_location",
                newName: "quest_locations");

            migrationBuilder.RenameTable(
                name: "quest",
                newName: "quests");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:game_mode_state", "cooldown,idle,queuing,resolving,running")
                .Annotation("Npgsql:Enum:game_mode_type", "casual,hardcore")
                .Annotation("Npgsql:Enum:geo_fence_shape", "circle,polygon")
                .Annotation("Npgsql:Enum:quest_instance_state", "active,cancelled,completed,expired")
                .Annotation("Npgsql:Enum:quest_type", "bounty,exploration,hunt")
                .Annotation("Npgsql:Enum:rank_requirement", "all_participants,top3,winner")
                .Annotation("Npgsql:Enum:reward_type", "cosmetic_unlock,full_diet,full_water,half_diet,half_water,xp")
                .Annotation("Npgsql:Enum:trigger_type", "admin_command,timer,zone_entry")
                .Annotation("Npgsql:PostgresExtension:hstore", ",,")
                .OldAnnotation("Npgsql:Enum:game_mode_state", "cooldown,idle,queuing,resolving,running")
                .OldAnnotation("Npgsql:Enum:game_mode_type", "casual,hardcore")
                .OldAnnotation("Npgsql:Enum:geo_fence_shape", "circle,polygon")
                .OldAnnotation("Npgsql:Enum:rank_requirement", "all_participants,top3,winner")
                .OldAnnotation("Npgsql:Enum:reward_type", "cosmetic_unlock,xp")
                .OldAnnotation("Npgsql:Enum:trigger_type", "admin_command,timer,zone_entry")
                .OldAnnotation("Npgsql:PostgresExtension:hstore", ",,");

            migrationBuilder.AddColumn<string>(
                name: "region_id",
                table: "quest_locations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "announcement_template",
                table: "quests",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "cooldown",
                table: "quests",
                type: "interval",
                nullable: false,
                defaultValue: new TimeSpan(0, 0, 0, 0, 0));

            migrationBuilder.AddColumn<TimeSpan>(
                name: "duration",
                table: "quests",
                type: "interval",
                nullable: false,
                defaultValue: new TimeSpan(0, 0, 0, 0, 0));

            migrationBuilder.AddColumn<bool>(
                name: "enabled",
                table: "quests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_spawned_at",
                table: "quests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "min_online_players",
                table: "quests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<QuestType>(
                name: "type",
                table: "quests",
                type: "quest_type",
                nullable: false,
                defaultValue: QuestType.Exploration);

            migrationBuilder.AddColumn<int>(
                name: "weight",
                table: "quests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddPrimaryKey(
                name: "pk_quest_locations",
                table: "quest_locations",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_quests",
                table: "quests",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_quests_rewards",
                table: "quests_rewards",
                columns: new[] { "quest_id", "id" });

            migrationBuilder.AddForeignKey(
                name: "fk_quests_rewards_quests_quest_id",
                table: "quests_rewards",
                column: "quest_id",
                principalTable: "quests",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.CreateTable(
                name: "quest_instances",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    quest_id = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<QuestType>(type: "quest_type", nullable: false),
                    state = table.Column<QuestInstanceState>(type: "quest_instance_state", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    location_id = table.Column<string>(type: "text", nullable: true),
                    region_id = table.Column<string>(type: "text", nullable: true),
                    location_name = table.Column<string>(type: "text", nullable: true),
                    world_x = table.Column<double>(type: "double precision", nullable: true),
                    world_y = table.Column<double>(type: "double precision", nullable: true),
                    target_player_id = table.Column<string>(type: "text", nullable: true),
                    target_species = table.Column<string>(type: "text", nullable: true),
                    completed_by_player_id = table.Column<string>(type: "text", nullable: true),
                    is_admin_spawned = table.Column<bool>(type: "boolean", nullable: false),
                    bonus_xp = table.Column<int>(type: "integer", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quest_instances", x => x.id);
                    table.ForeignKey(
                        name: "fk_quest_instances_players_completed_by_player_id",
                        column: x => x.completed_by_player_id,
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_quest_instances_players_target_player_id",
                        column: x => x.target_player_id,
                        principalTable: "players",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_quest_instances_quests_quest_id",
                        column: x => x.quest_id,
                        principalTable: "quests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_quest_instances_completed_by_player_id",
                table: "quest_instances",
                column: "completed_by_player_id");

            migrationBuilder.CreateIndex(
                name: "ix_quest_instances_quest_id",
                table: "quest_instances",
                column: "quest_id");

            migrationBuilder.CreateIndex(
                name: "ix_quest_instances_state_type",
                table: "quest_instances",
                columns: new[] { "state", "type" });

            migrationBuilder.CreateIndex(
                name: "ix_quest_instances_target_player_id",
                table: "quest_instances",
                column: "target_player_id");

            migrationBuilder.AddForeignKey(
                name: "fk_quest_quest_location_quest_locations_locations_id",
                table: "quest_quest_location",
                column: "locations_id",
                principalTable: "quest_locations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_quest_quest_location_quests_quests_id",
                table: "quest_quest_location",
                column: "quests_id",
                principalTable: "quests",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_quest_quest_location_quest_locations_locations_id",
                table: "quest_quest_location");

            migrationBuilder.DropForeignKey(
                name: "fk_quest_quest_location_quests_quests_id",
                table: "quest_quest_location");

            migrationBuilder.DropTable(
                name: "quest_instances");

            // Mirror of the Up() rename: the rewards table is carried back, not dropped.
            migrationBuilder.DropForeignKey(
                name: "fk_quests_rewards_quests_quest_id",
                table: "quests_rewards");

            migrationBuilder.DropPrimaryKey(
                name: "pk_quests_rewards",
                table: "quests_rewards");

            migrationBuilder.DropPrimaryKey(
                name: "pk_quests",
                table: "quests");

            migrationBuilder.DropPrimaryKey(
                name: "pk_quest_locations",
                table: "quest_locations");

            migrationBuilder.DropColumn(
                name: "announcement_template",
                table: "quests");

            migrationBuilder.DropColumn(
                name: "cooldown",
                table: "quests");

            migrationBuilder.DropColumn(
                name: "duration",
                table: "quests");

            migrationBuilder.DropColumn(
                name: "enabled",
                table: "quests");

            migrationBuilder.DropColumn(
                name: "last_spawned_at",
                table: "quests");

            migrationBuilder.DropColumn(
                name: "min_online_players",
                table: "quests");

            migrationBuilder.DropColumn(
                name: "type",
                table: "quests");

            migrationBuilder.DropColumn(
                name: "weight",
                table: "quests");

            migrationBuilder.DropColumn(
                name: "region_id",
                table: "quest_locations");

            migrationBuilder.RenameTable(
                name: "quests_rewards",
                newName: "quest_rewards");

            migrationBuilder.RenameTable(
                name: "quests",
                newName: "quest");

            migrationBuilder.RenameTable(
                name: "quest_locations",
                newName: "quest_location");

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
                .OldAnnotation("Npgsql:Enum:quest_instance_state", "active,cancelled,completed,expired")
                .OldAnnotation("Npgsql:Enum:quest_type", "bounty,exploration,hunt")
                .OldAnnotation("Npgsql:Enum:rank_requirement", "all_participants,top3,winner")
                .OldAnnotation("Npgsql:Enum:reward_type", "cosmetic_unlock,full_diet,full_water,half_diet,half_water,xp")
                .OldAnnotation("Npgsql:Enum:trigger_type", "admin_command,timer,zone_entry")
                .OldAnnotation("Npgsql:PostgresExtension:hstore", ",,");

            migrationBuilder.AddPrimaryKey(
                name: "pk_quest",
                table: "quest",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_quest_location",
                table: "quest_location",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_quest_rewards",
                table: "quest_rewards",
                columns: new[] { "quest_id", "id" });

            migrationBuilder.AddForeignKey(
                name: "fk_quest_rewards_quest_quest_id",
                table: "quest_rewards",
                column: "quest_id",
                principalTable: "quest",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_quest_quest_location_quest_location_locations_id",
                table: "quest_quest_location",
                column: "locations_id",
                principalTable: "quest_location",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_quest_quest_location_quest_quests_id",
                table: "quest_quest_location",
                column: "quests_id",
                principalTable: "quest",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
