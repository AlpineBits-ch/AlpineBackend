using System;
using System.Collections.Generic;
using Guild.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOnboardingPromptsWelcomeScreenAndForums : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_channels_parent_channel_id",
                table: "channels");

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
                .OldAnnotation("Npgsql:Enum:audit_action_type", "auto_mod_config_updated,auto_mod_message_blocked,bot_installed,bot_uninstalled,category_created,category_deleted,category_updated,channel_created,channel_deleted,channel_follow_created,channel_follow_removed,channel_permission_changed,channel_updated,emoji_created,emoji_deleted,guild_created_from_template,guild_deleted,guild_imported_from_discord,guild_synced_from_discord,guild_updated,invite_created,invite_deleted,member_banned,member_kicked,member_left,member_muted,member_unbanned,member_unmuted,message_pinned,message_unpinned,onboarding_config_updated,role_created,role_deleted,role_positions_changed,role_updated,scheduled_event_cancelled,scheduled_event_created,scheduled_event_deleted,scheduled_event_updated,template_created")
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

            migrationBuilder.AddColumn<OnboardingMode>(
                name: "mode",
                table: "guild_onboarding_configs",
                type: "onboarding_mode",
                nullable: false,
                defaultValue: OnboardingMode.Default);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "auto_archive_at",
                table: "channels",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "auto_archive_minutes",
                table: "channels",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_locked",
                table: "channels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_pinned",
                table: "channels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_activity_at",
                table: "channels",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "message_count",
                table: "channels",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "forum_configs",
                columns: table => new
                {
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    require_tag = table.Column<bool>(type: "boolean", nullable: false),
                    default_sort_order = table.Column<ForumSortOrder>(type: "forum_sort_order", nullable: false),
                    default_layout = table.Column<ForumLayout>(type: "forum_layout", nullable: false),
                    default_reaction_emoji_id = table.Column<string>(type: "text", nullable: true),
                    default_reaction_emoji_name = table.Column<string>(type: "text", nullable: true),
                    default_thread_slow_mode_seconds = table.Column<int>(type: "integer", nullable: false),
                    default_auto_archive_minutes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_forum_configs", x => x.channel_id);
                    table.ForeignKey(
                        name: "fk_forum_configs_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "forum_tags",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    emoji_id = table.Column<string>(type: "text", nullable: true),
                    emoji_name = table.Column<string>(type: "text", nullable: true),
                    color = table.Column<string>(type: "text", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    moderated = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_forum_tags", x => x.id);
                    table.ForeignKey(
                        name: "fk_forum_tags_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "guild_onboarding_grants",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    member_id = table.Column<string>(type: "text", nullable: false),
                    option_id = table.Column<string>(type: "text", nullable: false),
                    role_id = table.Column<string>(type: "text", nullable: true),
                    channel_id = table.Column<string>(type: "text", nullable: true),
                    channel_permission_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guild_onboarding_grants", x => x.id);
                    table.ForeignKey(
                        name: "fk_guild_onboarding_grants_guild_members_member_id",
                        column: x => x.member_id,
                        principalTable: "guild_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "guild_onboarding_prompts",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<OnboardingPromptType>(type: "onboarding_prompt_type", nullable: false),
                    single_select = table.Column<bool>(type: "boolean", nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: false),
                    in_onboarding = table.Column<bool>(type: "boolean", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guild_onboarding_prompts", x => x.id);
                    table.ForeignKey(
                        name: "fk_guild_onboarding_prompts_guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "guild_welcome_screens",
                columns: table => new
                {
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guild_welcome_screens", x => x.guild_id);
                    table.ForeignKey(
                        name: "fk_guild_welcome_screens_guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "forum_post_tags",
                columns: table => new
                {
                    thread_channel_id = table.Column<string>(type: "text", nullable: false),
                    tag_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_forum_post_tags", x => new { x.thread_channel_id, x.tag_id });
                    table.ForeignKey(
                        name: "fk_forum_post_tags_channels_thread_channel_id",
                        column: x => x.thread_channel_id,
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_forum_post_tags_forum_tags_tag_id",
                        column: x => x.tag_id,
                        principalTable: "forum_tags",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "guild_onboarding_prompt_options",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    prompt_id = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    emoji = table.Column<string>(type: "text", nullable: true),
                    role_ids = table.Column<List<string>>(type: "text[]", nullable: false),
                    channel_ids = table.Column<List<string>>(type: "text[]", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guild_onboarding_prompt_options", x => x.id);
                    table.ForeignKey(
                        name: "fk_guild_onboarding_prompt_options_guild_onboarding_prompts_pr",
                        column: x => x.prompt_id,
                        principalTable: "guild_onboarding_prompts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "guild_welcome_channels",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    emoji = table.Column<string>(type: "text", nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guild_welcome_channels", x => x.id);
                    table.ForeignKey(
                        name: "fk_guild_welcome_channels_guild_welcome_screens_guild_id",
                        column: x => x.guild_id,
                        principalTable: "guild_welcome_screens",
                        principalColumn: "guild_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "guild_member_onboarding_responses",
                columns: table => new
                {
                    member_id = table.Column<string>(type: "text", nullable: false),
                    option_id = table.Column<string>(type: "text", nullable: false),
                    prompt_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guild_member_onboarding_responses", x => new { x.member_id, x.option_id });
                    table.ForeignKey(
                        name: "fk_guild_member_onboarding_responses_guild_members_member_id",
                        column: x => x.member_id,
                        principalTable: "guild_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_guild_member_onboarding_responses_guild_onboarding_prompt_o",
                        column: x => x.option_id,
                        principalTable: "guild_onboarding_prompt_options",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_channels_forum_activity",
                table: "channels",
                columns: new[] { "parent_channel_id", "is_pinned", "last_activity_at" });

            migrationBuilder.CreateIndex(
                name: "IX_channels_forum_created",
                table: "channels",
                columns: new[] { "parent_channel_id", "is_pinned", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_forum_post_tags_tag_id",
                table: "forum_post_tags",
                column: "tag_id");

            migrationBuilder.CreateIndex(
                name: "ix_forum_tags_channel_id_name",
                table: "forum_tags",
                columns: new[] { "channel_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_forum_tags_channel_id_position",
                table: "forum_tags",
                columns: new[] { "channel_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_guild_member_onboarding_responses_option_id",
                table: "guild_member_onboarding_responses",
                column: "option_id");

            migrationBuilder.CreateIndex(
                name: "ix_guild_onboarding_grants_member_id_option_id",
                table: "guild_onboarding_grants",
                columns: new[] { "member_id", "option_id" });

            migrationBuilder.CreateIndex(
                name: "ix_guild_onboarding_prompt_options_prompt_id_position",
                table: "guild_onboarding_prompt_options",
                columns: new[] { "prompt_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_guild_onboarding_prompts_guild_id_position",
                table: "guild_onboarding_prompts",
                columns: new[] { "guild_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_guild_welcome_channels_guild_id_position",
                table: "guild_welcome_channels",
                columns: new[] { "guild_id", "position" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "forum_configs");

            migrationBuilder.DropTable(
                name: "forum_post_tags");

            migrationBuilder.DropTable(
                name: "guild_member_onboarding_responses");

            migrationBuilder.DropTable(
                name: "guild_onboarding_grants");

            migrationBuilder.DropTable(
                name: "guild_welcome_channels");

            migrationBuilder.DropTable(
                name: "forum_tags");

            migrationBuilder.DropTable(
                name: "guild_onboarding_prompt_options");

            migrationBuilder.DropTable(
                name: "guild_welcome_screens");

            migrationBuilder.DropTable(
                name: "guild_onboarding_prompts");

            migrationBuilder.DropIndex(
                name: "IX_channels_forum_activity",
                table: "channels");

            migrationBuilder.DropIndex(
                name: "IX_channels_forum_created",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "mode",
                table: "guild_onboarding_configs");

            migrationBuilder.DropColumn(
                name: "auto_archive_at",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "auto_archive_minutes",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "is_locked",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "is_pinned",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "last_activity_at",
                table: "channels");

            migrationBuilder.DropColumn(
                name: "message_count",
                table: "channels");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:audit_action_type", "auto_mod_config_updated,auto_mod_message_blocked,bot_installed,bot_uninstalled,category_created,category_deleted,category_updated,channel_created,channel_deleted,channel_follow_created,channel_follow_removed,channel_permission_changed,channel_updated,emoji_created,emoji_deleted,guild_created_from_template,guild_deleted,guild_imported_from_discord,guild_synced_from_discord,guild_updated,invite_created,invite_deleted,member_banned,member_kicked,member_left,member_muted,member_unbanned,member_unmuted,message_pinned,message_unpinned,onboarding_config_updated,role_created,role_deleted,role_positions_changed,role_updated,scheduled_event_cancelled,scheduled_event_created,scheduled_event_deleted,scheduled_event_updated,template_created")
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

            migrationBuilder.CreateIndex(
                name: "ix_channels_parent_channel_id",
                table: "channels",
                column: "parent_channel_id");
        }
    }
}
