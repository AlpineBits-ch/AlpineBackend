using Guild.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGuildKindAndFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:audit_action_type", "auto_mod_config_updated,auto_mod_message_blocked,bot_installed,bot_uninstalled,category_created,category_deleted,category_updated,channel_created,channel_deleted,channel_follow_created,channel_follow_removed,channel_permission_changed,channel_updated,emoji_created,emoji_deleted,forum_config_updated,forum_tag_created,forum_tag_deleted,forum_tag_updated,forum_tags_reordered,guild_created_from_template,guild_deleted,guild_imported_from_discord,guild_synced_from_discord,guild_updated,invite_created,invite_deleted,member_banned,member_kicked,member_left,member_muted,member_unbanned,member_unmuted,message_pinned,message_unpinned,onboarding_config_updated,onboarding_prompt_created,onboarding_prompt_deleted,onboarding_prompt_updated,role_created,role_deleted,role_positions_changed,role_updated,scheduled_event_cancelled,scheduled_event_created,scheduled_event_deleted,scheduled_event_updated,template_created,thread_lock_changed,thread_pin_changed,thread_tags_updated,welcome_screen_updated")
                .Annotation("Npgsql:Enum:channel_type", "announcement,forum,media,text,thread,ticket,voice")
                .Annotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .Annotation("Npgsql:Enum:forum_layout", "gallery,list")
                .Annotation("Npgsql:Enum:forum_sort_order", "creation_date,latest_activity")
                .Annotation("Npgsql:Enum:guild_kind", "community,event,household,study,team")
                .Annotation("Npgsql:Enum:guild_scheduled_event_status", "active,cancelled,completed,scheduled")
                .Annotation("Npgsql:Enum:guild_verification_level", "high,low,medium,none")
                .Annotation("Npgsql:Enum:invite_state", "active,expired")
                .Annotation("Npgsql:Enum:invite_type", "one_time,permanent")
                .Annotation("Npgsql:Enum:member_type", "bot,default,persona")
                .Annotation("Npgsql:Enum:onboarding_mode", "advanced,default")
                .Annotation("Npgsql:Enum:onboarding_prompt_type", "dropdown,multiple_choice")
                .Annotation("Npgsql:Enum:permission_state", "allow,deny,inherit")
                .Annotation("Npgsql:Enum:role_type", "everyone,none")
                .Annotation("Npgsql:Enum:wiki_visibility", "private,public")
                .OldAnnotation("Npgsql:Enum:audit_action_type", "auto_mod_config_updated,auto_mod_message_blocked,bot_installed,bot_uninstalled,category_created,category_deleted,category_updated,channel_created,channel_deleted,channel_follow_created,channel_follow_removed,channel_permission_changed,channel_updated,emoji_created,emoji_deleted,forum_config_updated,forum_tag_created,forum_tag_deleted,forum_tag_updated,forum_tags_reordered,guild_created_from_template,guild_deleted,guild_imported_from_discord,guild_synced_from_discord,guild_updated,invite_created,invite_deleted,member_banned,member_kicked,member_left,member_muted,member_unbanned,member_unmuted,message_pinned,message_unpinned,onboarding_config_updated,onboarding_prompt_created,onboarding_prompt_deleted,onboarding_prompt_updated,role_created,role_deleted,role_positions_changed,role_updated,scheduled_event_cancelled,scheduled_event_created,scheduled_event_deleted,scheduled_event_updated,template_created,thread_lock_changed,thread_pin_changed,thread_tags_updated,welcome_screen_updated")
                .OldAnnotation("Npgsql:Enum:channel_type", "announcement,forum,media,text,thread,ticket,voice")
                .OldAnnotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .OldAnnotation("Npgsql:Enum:forum_layout", "gallery,list")
                .OldAnnotation("Npgsql:Enum:forum_sort_order", "creation_date,latest_activity")
                .OldAnnotation("Npgsql:Enum:guild_scheduled_event_status", "active,cancelled,completed,scheduled")
                .OldAnnotation("Npgsql:Enum:guild_verification_level", "high,low,medium,none")
                .OldAnnotation("Npgsql:Enum:invite_state", "active,expired")
                .OldAnnotation("Npgsql:Enum:invite_type", "one_time,permanent")
                .OldAnnotation("Npgsql:Enum:member_type", "bot,default,persona")
                .OldAnnotation("Npgsql:Enum:onboarding_mode", "advanced,default")
                .OldAnnotation("Npgsql:Enum:onboarding_prompt_type", "dropdown,multiple_choice")
                .OldAnnotation("Npgsql:Enum:permission_state", "allow,deny,inherit")
                .OldAnnotation("Npgsql:Enum:role_type", "everyone,none")
                .OldAnnotation("Npgsql:Enum:wiki_visibility", "private,public");

            migrationBuilder.AddColumn<decimal>(
                name: "features",
                table: "guilds",
                type: "numeric(20,0)",
                nullable: false,
                defaultValue: 0m);

            // EF scaffolds the column default as 0, which for a [Flags] mask means "every module
            // off" - applying that as-is would have silently stripped voice, threads, forums,
            // moderation, wiki and events from every guild that already exists the moment the
            // permission gate went live. Backfill them to GuildFeaturePresets.Community (bits
            // 0-11 == 4095), which is exactly the behaviour they have today.
            //
            // Done as an UPDATE rather than by raising the column default, so the schema doesn't
            // carry a default the EF model has no knowledge of. Keep the literal in sync with
            // GuildFeaturePresets.Community if it ever gains a flag.
            migrationBuilder.Sql("UPDATE guilds SET features = 4095;");

            migrationBuilder.AddColumn<GuildKind>(
                name: "kind",
                table: "guilds",
                type: "guild_kind",
                nullable: false,
                defaultValue: GuildKind.Community);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "features",
                table: "guilds");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "guilds");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:audit_action_type", "auto_mod_config_updated,auto_mod_message_blocked,bot_installed,bot_uninstalled,category_created,category_deleted,category_updated,channel_created,channel_deleted,channel_follow_created,channel_follow_removed,channel_permission_changed,channel_updated,emoji_created,emoji_deleted,forum_config_updated,forum_tag_created,forum_tag_deleted,forum_tag_updated,forum_tags_reordered,guild_created_from_template,guild_deleted,guild_imported_from_discord,guild_synced_from_discord,guild_updated,invite_created,invite_deleted,member_banned,member_kicked,member_left,member_muted,member_unbanned,member_unmuted,message_pinned,message_unpinned,onboarding_config_updated,onboarding_prompt_created,onboarding_prompt_deleted,onboarding_prompt_updated,role_created,role_deleted,role_positions_changed,role_updated,scheduled_event_cancelled,scheduled_event_created,scheduled_event_deleted,scheduled_event_updated,template_created,thread_lock_changed,thread_pin_changed,thread_tags_updated,welcome_screen_updated")
                .Annotation("Npgsql:Enum:channel_type", "announcement,forum,media,text,thread,ticket,voice")
                .Annotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .Annotation("Npgsql:Enum:forum_layout", "gallery,list")
                .Annotation("Npgsql:Enum:forum_sort_order", "creation_date,latest_activity")
                .Annotation("Npgsql:Enum:guild_scheduled_event_status", "active,cancelled,completed,scheduled")
                .Annotation("Npgsql:Enum:guild_verification_level", "high,low,medium,none")
                .Annotation("Npgsql:Enum:invite_state", "active,expired")
                .Annotation("Npgsql:Enum:invite_type", "one_time,permanent")
                .Annotation("Npgsql:Enum:member_type", "bot,default,persona")
                .Annotation("Npgsql:Enum:onboarding_mode", "advanced,default")
                .Annotation("Npgsql:Enum:onboarding_prompt_type", "dropdown,multiple_choice")
                .Annotation("Npgsql:Enum:permission_state", "allow,deny,inherit")
                .Annotation("Npgsql:Enum:role_type", "everyone,none")
                .Annotation("Npgsql:Enum:wiki_visibility", "private,public")
                .OldAnnotation("Npgsql:Enum:audit_action_type", "auto_mod_config_updated,auto_mod_message_blocked,bot_installed,bot_uninstalled,category_created,category_deleted,category_updated,channel_created,channel_deleted,channel_follow_created,channel_follow_removed,channel_permission_changed,channel_updated,emoji_created,emoji_deleted,forum_config_updated,forum_tag_created,forum_tag_deleted,forum_tag_updated,forum_tags_reordered,guild_created_from_template,guild_deleted,guild_imported_from_discord,guild_synced_from_discord,guild_updated,invite_created,invite_deleted,member_banned,member_kicked,member_left,member_muted,member_unbanned,member_unmuted,message_pinned,message_unpinned,onboarding_config_updated,onboarding_prompt_created,onboarding_prompt_deleted,onboarding_prompt_updated,role_created,role_deleted,role_positions_changed,role_updated,scheduled_event_cancelled,scheduled_event_created,scheduled_event_deleted,scheduled_event_updated,template_created,thread_lock_changed,thread_pin_changed,thread_tags_updated,welcome_screen_updated")
                .OldAnnotation("Npgsql:Enum:channel_type", "announcement,forum,media,text,thread,ticket,voice")
                .OldAnnotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .OldAnnotation("Npgsql:Enum:forum_layout", "gallery,list")
                .OldAnnotation("Npgsql:Enum:forum_sort_order", "creation_date,latest_activity")
                .OldAnnotation("Npgsql:Enum:guild_kind", "community,event,household,study,team")
                .OldAnnotation("Npgsql:Enum:guild_scheduled_event_status", "active,cancelled,completed,scheduled")
                .OldAnnotation("Npgsql:Enum:guild_verification_level", "high,low,medium,none")
                .OldAnnotation("Npgsql:Enum:invite_state", "active,expired")
                .OldAnnotation("Npgsql:Enum:invite_type", "one_time,permanent")
                .OldAnnotation("Npgsql:Enum:member_type", "bot,default,persona")
                .OldAnnotation("Npgsql:Enum:onboarding_mode", "advanced,default")
                .OldAnnotation("Npgsql:Enum:onboarding_prompt_type", "dropdown,multiple_choice")
                .OldAnnotation("Npgsql:Enum:permission_state", "allow,deny,inherit")
                .OldAnnotation("Npgsql:Enum:role_type", "everyone,none")
                .OldAnnotation("Npgsql:Enum:wiki_visibility", "private,public");
        }
    }
}
