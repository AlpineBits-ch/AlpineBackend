using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGuildChannelFollows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:audit_action_type", "auto_mod_config_updated,auto_mod_message_blocked,bot_installed,bot_uninstalled,category_created,category_deleted,channel_created,channel_deleted,channel_follow_created,channel_follow_removed,channel_permission_changed,channel_updated,emoji_created,emoji_deleted,guild_created_from_template,guild_deleted,guild_imported_from_discord,guild_synced_from_discord,guild_updated,invite_created,invite_deleted,member_banned,member_kicked,member_left,member_muted,member_unbanned,member_unmuted,message_pinned,message_unpinned,onboarding_config_updated,role_created,role_deleted,role_positions_changed,role_updated,scheduled_event_cancelled,scheduled_event_created,scheduled_event_deleted,scheduled_event_updated,template_created")
                .Annotation("Npgsql:Enum:channel_type", "announcement,forum,text,thread,ticket,voice")
                .Annotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .Annotation("Npgsql:Enum:guild_scheduled_event_status", "active,cancelled,completed,scheduled")
                .Annotation("Npgsql:Enum:guild_verification_level", "high,low,medium,none")
                .Annotation("Npgsql:Enum:invite_state", "active,expired")
                .Annotation("Npgsql:Enum:invite_type", "one_time,permanent")
                .Annotation("Npgsql:Enum:member_type", "bot,default,persona")
                .Annotation("Npgsql:Enum:permission_state", "allow,deny,inherit")
                .Annotation("Npgsql:Enum:role_type", "everyone,none")
                .Annotation("Npgsql:Enum:wiki_visibility", "private,public")
                .OldAnnotation("Npgsql:Enum:audit_action_type", "auto_mod_config_updated,auto_mod_message_blocked,bot_installed,bot_uninstalled,category_created,category_deleted,channel_created,channel_deleted,channel_permission_changed,channel_updated,emoji_created,emoji_deleted,guild_created_from_template,guild_deleted,guild_imported_from_discord,guild_synced_from_discord,guild_updated,invite_created,invite_deleted,member_banned,member_kicked,member_left,member_muted,member_unbanned,member_unmuted,message_pinned,message_unpinned,onboarding_config_updated,role_created,role_deleted,role_positions_changed,role_updated,scheduled_event_cancelled,scheduled_event_created,scheduled_event_deleted,scheduled_event_updated,template_created")
                .OldAnnotation("Npgsql:Enum:channel_type", "announcement,forum,text,thread,ticket,voice")
                .OldAnnotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .OldAnnotation("Npgsql:Enum:guild_scheduled_event_status", "active,cancelled,completed,scheduled")
                .OldAnnotation("Npgsql:Enum:guild_verification_level", "high,low,medium,none")
                .OldAnnotation("Npgsql:Enum:invite_state", "active,expired")
                .OldAnnotation("Npgsql:Enum:invite_type", "one_time,permanent")
                .OldAnnotation("Npgsql:Enum:member_type", "bot,default,persona")
                .OldAnnotation("Npgsql:Enum:permission_state", "allow,deny,inherit")
                .OldAnnotation("Npgsql:Enum:role_type", "everyone,none")
                .OldAnnotation("Npgsql:Enum:wiki_visibility", "private,public");

            migrationBuilder.CreateTable(
                name: "guild_channel_follows",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    source_channel_id = table.Column<string>(type: "text", nullable: false),
                    source_guild_id = table.Column<string>(type: "text", nullable: false),
                    target_channel_id = table.Column<string>(type: "text", nullable: false),
                    target_guild_id = table.Column<string>(type: "text", nullable: false),
                    created_by_user_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guild_channel_follows", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_guild_channel_follows_source_channel_id",
                table: "guild_channel_follows",
                column: "source_channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_guild_channel_follows_source_channel_id_target_channel_id",
                table: "guild_channel_follows",
                columns: new[] { "source_channel_id", "target_channel_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "guild_channel_follows");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:audit_action_type", "auto_mod_config_updated,auto_mod_message_blocked,bot_installed,bot_uninstalled,category_created,category_deleted,channel_created,channel_deleted,channel_permission_changed,channel_updated,emoji_created,emoji_deleted,guild_created_from_template,guild_deleted,guild_imported_from_discord,guild_synced_from_discord,guild_updated,invite_created,invite_deleted,member_banned,member_kicked,member_left,member_muted,member_unbanned,member_unmuted,message_pinned,message_unpinned,onboarding_config_updated,role_created,role_deleted,role_positions_changed,role_updated,scheduled_event_cancelled,scheduled_event_created,scheduled_event_deleted,scheduled_event_updated,template_created")
                .Annotation("Npgsql:Enum:channel_type", "announcement,forum,text,thread,ticket,voice")
                .Annotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .Annotation("Npgsql:Enum:guild_scheduled_event_status", "active,cancelled,completed,scheduled")
                .Annotation("Npgsql:Enum:guild_verification_level", "high,low,medium,none")
                .Annotation("Npgsql:Enum:invite_state", "active,expired")
                .Annotation("Npgsql:Enum:invite_type", "one_time,permanent")
                .Annotation("Npgsql:Enum:member_type", "bot,default,persona")
                .Annotation("Npgsql:Enum:permission_state", "allow,deny,inherit")
                .Annotation("Npgsql:Enum:role_type", "everyone,none")
                .Annotation("Npgsql:Enum:wiki_visibility", "private,public")
                .OldAnnotation("Npgsql:Enum:audit_action_type", "auto_mod_config_updated,auto_mod_message_blocked,bot_installed,bot_uninstalled,category_created,category_deleted,channel_created,channel_deleted,channel_follow_created,channel_follow_removed,channel_permission_changed,channel_updated,emoji_created,emoji_deleted,guild_created_from_template,guild_deleted,guild_imported_from_discord,guild_synced_from_discord,guild_updated,invite_created,invite_deleted,member_banned,member_kicked,member_left,member_muted,member_unbanned,member_unmuted,message_pinned,message_unpinned,onboarding_config_updated,role_created,role_deleted,role_positions_changed,role_updated,scheduled_event_cancelled,scheduled_event_created,scheduled_event_deleted,scheduled_event_updated,template_created")
                .OldAnnotation("Npgsql:Enum:channel_type", "announcement,forum,text,thread,ticket,voice")
                .OldAnnotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .OldAnnotation("Npgsql:Enum:guild_scheduled_event_status", "active,cancelled,completed,scheduled")
                .OldAnnotation("Npgsql:Enum:guild_verification_level", "high,low,medium,none")
                .OldAnnotation("Npgsql:Enum:invite_state", "active,expired")
                .OldAnnotation("Npgsql:Enum:invite_type", "one_time,permanent")
                .OldAnnotation("Npgsql:Enum:member_type", "bot,default,persona")
                .OldAnnotation("Npgsql:Enum:permission_state", "allow,deny,inherit")
                .OldAnnotation("Npgsql:Enum:role_type", "everyone,none")
                .OldAnnotation("Npgsql:Enum:wiki_visibility", "private,public");
        }
    }
}
