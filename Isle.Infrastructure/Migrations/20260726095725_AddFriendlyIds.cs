using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Isle.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFriendlyIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                .OldAnnotation("Npgsql:Enum:quest_instance_state", "active,cancelled,completed,expired")
                .OldAnnotation("Npgsql:Enum:quest_type", "bounty,exploration,hunt")
                .OldAnnotation("Npgsql:Enum:rank_requirement", "all_participants,top3,winner")
                .OldAnnotation("Npgsql:Enum:reward_type", "cosmetic_unlock,full_diet,full_water,half_diet,half_water,xp")
                .OldAnnotation("Npgsql:Enum:trigger_type", "admin_command,timer,zone_entry")
                .OldAnnotation("Npgsql:PostgresExtension:hstore", ",,");

            migrationBuilder.CreateSequence(
                name: "quest_instance_friendly_id_seq",
                startValue: 1000L);

            migrationBuilder.AddColumn<int>(
                name: "friendly_id_seq",
                table: "quest_instances",
                type: "integer",
                nullable: false,
                defaultValueSql: "nextval('quest_instance_friendly_id_seq')");

            migrationBuilder.CreateIndex(
                name: "ix_quest_instances_friendly_id_seq",
                table: "quest_instances",
                column: "friendly_id_seq",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_quest_instances_friendly_id_seq",
                table: "quest_instances");

            migrationBuilder.DropColumn(
                name: "friendly_id_seq",
                table: "quest_instances");

            migrationBuilder.DropSequence(
                name: "quest_instance_friendly_id_seq");

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
                .OldAnnotation("Npgsql:Enum:quest_instance_state", "active,cancelled,completed,expired")
                .OldAnnotation("Npgsql:Enum:quest_type", "bounty,exploration,hunt")
                .OldAnnotation("Npgsql:Enum:rank_requirement", "all_participants,top3,winner")
                .OldAnnotation("Npgsql:Enum:reward_type", "cosmetic_unlock,full_diet,full_health,full_stamina,full_water,growth_boost,half_diet,half_water,storage_slot,xp")
                .OldAnnotation("Npgsql:Enum:trigger_type", "admin_command,timer,zone_entry")
                .OldAnnotation("Npgsql:PostgresExtension:hstore", ",,");
        }
    }
}
