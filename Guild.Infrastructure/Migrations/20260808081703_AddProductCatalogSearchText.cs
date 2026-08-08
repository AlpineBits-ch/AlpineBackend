using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductCatalogSearchText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:asset_status", "broken,needs_attention,ok,out_of_service")
                .Annotation("Npgsql:Enum:audit_action_type", "auto_mod_config_updated,auto_mod_message_blocked,bill_posted,bill_skipped,bot_installed,bot_uninstalled,category_created,category_deleted,category_updated,channel_created,channel_deleted,channel_follow_created,channel_follow_removed,channel_permission_changed,channel_updated,emoji_created,emoji_deleted,expense_created,expense_deleted,expense_updated,forum_config_updated,forum_tag_created,forum_tag_deleted,forum_tag_updated,forum_tags_reordered,guild_created_from_template,guild_deleted,guild_imported_from_discord,guild_synced_from_discord,guild_updated,invite_created,invite_deleted,ledger_config_updated,maintenance_asset_created,maintenance_asset_deleted,maintenance_asset_updated,maintenance_record_created,member_banned,member_kicked,member_left,member_moved_out,member_muted,member_nickname_changed,member_unbanned,member_unmuted,message_pinned,message_unpinned,onboarding_config_updated,onboarding_prompt_created,onboarding_prompt_deleted,onboarding_prompt_updated,recurring_expense_created,recurring_expense_deleted,recurring_expense_updated,role_created,role_deleted,role_positions_changed,role_updated,scheduled_event_cancelled,scheduled_event_created,scheduled_event_deleted,scheduled_event_updated,settlement_recorded,template_created,thread_lock_changed,thread_pin_changed,thread_tags_updated,welcome_screen_updated")
                .Annotation("Npgsql:Enum:bill_status", "pending,posted,skipped")
                .Annotation("Npgsql:Enum:channel_type", "announcement,chores,decisions,forum,ledger,list,maintenance,meals,media,pantry,text,thread,ticket,voice")
                .Annotation("Npgsql:Enum:decision_status", "blocked,cancelled,decided,expired,open")
                .Annotation("Npgsql:Enum:decision_vote_kind", "abstain,block,support")
                .Annotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .Annotation("Npgsql:Enum:expense_category", "eating_out,entertainment,groceries,health,household,internet,other,pets,rent,repairs,transport,uncategorized,utilities")
                .Annotation("Npgsql:Enum:expense_split_kind", "equal,exact,shares")
                .Annotation("Npgsql:Enum:forum_layout", "gallery,list")
                .Annotation("Npgsql:Enum:forum_sort_order", "creation_date,latest_activity")
                .Annotation("Npgsql:Enum:guild_kind", "community,event,household,study,team")
                .Annotation("Npgsql:Enum:guild_scheduled_event_status", "active,cancelled,completed,scheduled")
                .Annotation("Npgsql:Enum:guild_verification_level", "high,low,medium,none")
                .Annotation("Npgsql:Enum:invite_state", "active,expired")
                .Annotation("Npgsql:Enum:invite_type", "one_time,permanent")
                .Annotation("Npgsql:Enum:meal_slot", "breakfast,dinner,lunch,other")
                .Annotation("Npgsql:Enum:member_type", "bot,default,persona")
                .Annotation("Npgsql:Enum:onboarding_mode", "advanced,default")
                .Annotation("Npgsql:Enum:onboarding_prompt_type", "dropdown,multiple_choice")
                .Annotation("Npgsql:Enum:permission_state", "allow,deny,inherit")
                .Annotation("Npgsql:Enum:recurrence_unit", "day,month,week,year")
                .Annotation("Npgsql:Enum:role_type", "everyone,none")
                .Annotation("Npgsql:Enum:wiki_visibility", "private,public")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:Enum:asset_status", "broken,needs_attention,ok,out_of_service")
                .OldAnnotation("Npgsql:Enum:audit_action_type", "auto_mod_config_updated,auto_mod_message_blocked,bill_posted,bill_skipped,bot_installed,bot_uninstalled,category_created,category_deleted,category_updated,channel_created,channel_deleted,channel_follow_created,channel_follow_removed,channel_permission_changed,channel_updated,emoji_created,emoji_deleted,expense_created,expense_deleted,expense_updated,forum_config_updated,forum_tag_created,forum_tag_deleted,forum_tag_updated,forum_tags_reordered,guild_created_from_template,guild_deleted,guild_imported_from_discord,guild_synced_from_discord,guild_updated,invite_created,invite_deleted,ledger_config_updated,maintenance_asset_created,maintenance_asset_deleted,maintenance_asset_updated,maintenance_record_created,member_banned,member_kicked,member_left,member_moved_out,member_muted,member_nickname_changed,member_unbanned,member_unmuted,message_pinned,message_unpinned,onboarding_config_updated,onboarding_prompt_created,onboarding_prompt_deleted,onboarding_prompt_updated,recurring_expense_created,recurring_expense_deleted,recurring_expense_updated,role_created,role_deleted,role_positions_changed,role_updated,scheduled_event_cancelled,scheduled_event_created,scheduled_event_deleted,scheduled_event_updated,settlement_recorded,template_created,thread_lock_changed,thread_pin_changed,thread_tags_updated,welcome_screen_updated")
                .OldAnnotation("Npgsql:Enum:bill_status", "pending,posted,skipped")
                .OldAnnotation("Npgsql:Enum:channel_type", "announcement,chores,decisions,forum,ledger,list,maintenance,meals,media,pantry,text,thread,ticket,voice")
                .OldAnnotation("Npgsql:Enum:decision_status", "blocked,cancelled,decided,expired,open")
                .OldAnnotation("Npgsql:Enum:decision_vote_kind", "abstain,block,support")
                .OldAnnotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .OldAnnotation("Npgsql:Enum:expense_category", "eating_out,entertainment,groceries,health,household,internet,other,pets,rent,repairs,transport,uncategorized,utilities")
                .OldAnnotation("Npgsql:Enum:expense_split_kind", "equal,exact,shares")
                .OldAnnotation("Npgsql:Enum:forum_layout", "gallery,list")
                .OldAnnotation("Npgsql:Enum:forum_sort_order", "creation_date,latest_activity")
                .OldAnnotation("Npgsql:Enum:guild_kind", "community,event,household,study,team")
                .OldAnnotation("Npgsql:Enum:guild_scheduled_event_status", "active,cancelled,completed,scheduled")
                .OldAnnotation("Npgsql:Enum:guild_verification_level", "high,low,medium,none")
                .OldAnnotation("Npgsql:Enum:invite_state", "active,expired")
                .OldAnnotation("Npgsql:Enum:invite_type", "one_time,permanent")
                .OldAnnotation("Npgsql:Enum:meal_slot", "breakfast,dinner,lunch,other")
                .OldAnnotation("Npgsql:Enum:member_type", "bot,default,persona")
                .OldAnnotation("Npgsql:Enum:onboarding_mode", "advanced,default")
                .OldAnnotation("Npgsql:Enum:onboarding_prompt_type", "dropdown,multiple_choice")
                .OldAnnotation("Npgsql:Enum:permission_state", "allow,deny,inherit")
                .OldAnnotation("Npgsql:Enum:recurrence_unit", "day,month,week,year")
                .OldAnnotation("Npgsql:Enum:role_type", "everyone,none")
                .OldAnnotation("Npgsql:Enum:wiki_visibility", "private,public");

            migrationBuilder.AddColumn<string>(
                name: "search_text",
                table: "product_catalog_entries",
                type: "text",
                nullable: true,
                computedColumnSql: "coalesce(name_de, '') || ' ' || coalesce(name_fr, '') || ' ' ||\r\ncoalesce(name_it, '') || ' ' || coalesce(name_en, '') || ' ' ||\r\ncoalesce(brand, '')",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "ix_product_catalog_entries_search_text",
                table: "product_catalog_entries",
                column: "search_text")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_product_catalog_entries_search_text",
                table: "product_catalog_entries");

            migrationBuilder.DropColumn(
                name: "search_text",
                table: "product_catalog_entries");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:asset_status", "broken,needs_attention,ok,out_of_service")
                .Annotation("Npgsql:Enum:audit_action_type", "auto_mod_config_updated,auto_mod_message_blocked,bill_posted,bill_skipped,bot_installed,bot_uninstalled,category_created,category_deleted,category_updated,channel_created,channel_deleted,channel_follow_created,channel_follow_removed,channel_permission_changed,channel_updated,emoji_created,emoji_deleted,expense_created,expense_deleted,expense_updated,forum_config_updated,forum_tag_created,forum_tag_deleted,forum_tag_updated,forum_tags_reordered,guild_created_from_template,guild_deleted,guild_imported_from_discord,guild_synced_from_discord,guild_updated,invite_created,invite_deleted,ledger_config_updated,maintenance_asset_created,maintenance_asset_deleted,maintenance_asset_updated,maintenance_record_created,member_banned,member_kicked,member_left,member_moved_out,member_muted,member_nickname_changed,member_unbanned,member_unmuted,message_pinned,message_unpinned,onboarding_config_updated,onboarding_prompt_created,onboarding_prompt_deleted,onboarding_prompt_updated,recurring_expense_created,recurring_expense_deleted,recurring_expense_updated,role_created,role_deleted,role_positions_changed,role_updated,scheduled_event_cancelled,scheduled_event_created,scheduled_event_deleted,scheduled_event_updated,settlement_recorded,template_created,thread_lock_changed,thread_pin_changed,thread_tags_updated,welcome_screen_updated")
                .Annotation("Npgsql:Enum:bill_status", "pending,posted,skipped")
                .Annotation("Npgsql:Enum:channel_type", "announcement,chores,decisions,forum,ledger,list,maintenance,meals,media,pantry,text,thread,ticket,voice")
                .Annotation("Npgsql:Enum:decision_status", "blocked,cancelled,decided,expired,open")
                .Annotation("Npgsql:Enum:decision_vote_kind", "abstain,block,support")
                .Annotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .Annotation("Npgsql:Enum:expense_category", "eating_out,entertainment,groceries,health,household,internet,other,pets,rent,repairs,transport,uncategorized,utilities")
                .Annotation("Npgsql:Enum:expense_split_kind", "equal,exact,shares")
                .Annotation("Npgsql:Enum:forum_layout", "gallery,list")
                .Annotation("Npgsql:Enum:forum_sort_order", "creation_date,latest_activity")
                .Annotation("Npgsql:Enum:guild_kind", "community,event,household,study,team")
                .Annotation("Npgsql:Enum:guild_scheduled_event_status", "active,cancelled,completed,scheduled")
                .Annotation("Npgsql:Enum:guild_verification_level", "high,low,medium,none")
                .Annotation("Npgsql:Enum:invite_state", "active,expired")
                .Annotation("Npgsql:Enum:invite_type", "one_time,permanent")
                .Annotation("Npgsql:Enum:meal_slot", "breakfast,dinner,lunch,other")
                .Annotation("Npgsql:Enum:member_type", "bot,default,persona")
                .Annotation("Npgsql:Enum:onboarding_mode", "advanced,default")
                .Annotation("Npgsql:Enum:onboarding_prompt_type", "dropdown,multiple_choice")
                .Annotation("Npgsql:Enum:permission_state", "allow,deny,inherit")
                .Annotation("Npgsql:Enum:recurrence_unit", "day,month,week,year")
                .Annotation("Npgsql:Enum:role_type", "everyone,none")
                .Annotation("Npgsql:Enum:wiki_visibility", "private,public")
                .OldAnnotation("Npgsql:Enum:asset_status", "broken,needs_attention,ok,out_of_service")
                .OldAnnotation("Npgsql:Enum:audit_action_type", "auto_mod_config_updated,auto_mod_message_blocked,bill_posted,bill_skipped,bot_installed,bot_uninstalled,category_created,category_deleted,category_updated,channel_created,channel_deleted,channel_follow_created,channel_follow_removed,channel_permission_changed,channel_updated,emoji_created,emoji_deleted,expense_created,expense_deleted,expense_updated,forum_config_updated,forum_tag_created,forum_tag_deleted,forum_tag_updated,forum_tags_reordered,guild_created_from_template,guild_deleted,guild_imported_from_discord,guild_synced_from_discord,guild_updated,invite_created,invite_deleted,ledger_config_updated,maintenance_asset_created,maintenance_asset_deleted,maintenance_asset_updated,maintenance_record_created,member_banned,member_kicked,member_left,member_moved_out,member_muted,member_nickname_changed,member_unbanned,member_unmuted,message_pinned,message_unpinned,onboarding_config_updated,onboarding_prompt_created,onboarding_prompt_deleted,onboarding_prompt_updated,recurring_expense_created,recurring_expense_deleted,recurring_expense_updated,role_created,role_deleted,role_positions_changed,role_updated,scheduled_event_cancelled,scheduled_event_created,scheduled_event_deleted,scheduled_event_updated,settlement_recorded,template_created,thread_lock_changed,thread_pin_changed,thread_tags_updated,welcome_screen_updated")
                .OldAnnotation("Npgsql:Enum:bill_status", "pending,posted,skipped")
                .OldAnnotation("Npgsql:Enum:channel_type", "announcement,chores,decisions,forum,ledger,list,maintenance,meals,media,pantry,text,thread,ticket,voice")
                .OldAnnotation("Npgsql:Enum:decision_status", "blocked,cancelled,decided,expired,open")
                .OldAnnotation("Npgsql:Enum:decision_vote_kind", "abstain,block,support")
                .OldAnnotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .OldAnnotation("Npgsql:Enum:expense_category", "eating_out,entertainment,groceries,health,household,internet,other,pets,rent,repairs,transport,uncategorized,utilities")
                .OldAnnotation("Npgsql:Enum:expense_split_kind", "equal,exact,shares")
                .OldAnnotation("Npgsql:Enum:forum_layout", "gallery,list")
                .OldAnnotation("Npgsql:Enum:forum_sort_order", "creation_date,latest_activity")
                .OldAnnotation("Npgsql:Enum:guild_kind", "community,event,household,study,team")
                .OldAnnotation("Npgsql:Enum:guild_scheduled_event_status", "active,cancelled,completed,scheduled")
                .OldAnnotation("Npgsql:Enum:guild_verification_level", "high,low,medium,none")
                .OldAnnotation("Npgsql:Enum:invite_state", "active,expired")
                .OldAnnotation("Npgsql:Enum:invite_type", "one_time,permanent")
                .OldAnnotation("Npgsql:Enum:meal_slot", "breakfast,dinner,lunch,other")
                .OldAnnotation("Npgsql:Enum:member_type", "bot,default,persona")
                .OldAnnotation("Npgsql:Enum:onboarding_mode", "advanced,default")
                .OldAnnotation("Npgsql:Enum:onboarding_prompt_type", "dropdown,multiple_choice")
                .OldAnnotation("Npgsql:Enum:permission_state", "allow,deny,inherit")
                .OldAnnotation("Npgsql:Enum:recurrence_unit", "day,month,week,year")
                .OldAnnotation("Npgsql:Enum:role_type", "everyone,none")
                .OldAnnotation("Npgsql:Enum:wiki_visibility", "private,public")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");
        }
    }
}
