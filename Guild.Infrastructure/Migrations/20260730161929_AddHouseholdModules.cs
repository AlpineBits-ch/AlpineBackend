using System;
using Guild.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHouseholdModules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:audit_action_type", "auto_mod_config_updated,auto_mod_message_blocked,bot_installed,bot_uninstalled,category_created,category_deleted,category_updated,channel_created,channel_deleted,channel_follow_created,channel_follow_removed,channel_permission_changed,channel_updated,emoji_created,emoji_deleted,forum_config_updated,forum_tag_created,forum_tag_deleted,forum_tag_updated,forum_tags_reordered,guild_created_from_template,guild_deleted,guild_imported_from_discord,guild_synced_from_discord,guild_updated,invite_created,invite_deleted,member_banned,member_kicked,member_left,member_muted,member_unbanned,member_unmuted,message_pinned,message_unpinned,onboarding_config_updated,onboarding_prompt_created,onboarding_prompt_deleted,onboarding_prompt_updated,role_created,role_deleted,role_positions_changed,role_updated,scheduled_event_cancelled,scheduled_event_created,scheduled_event_deleted,scheduled_event_updated,template_created,thread_lock_changed,thread_pin_changed,thread_tags_updated,welcome_screen_updated")
                .Annotation("Npgsql:Enum:channel_type", "announcement,chores,decisions,forum,ledger,list,media,pantry,text,thread,ticket,voice")
                .Annotation("Npgsql:Enum:decision_status", "blocked,cancelled,decided,expired,open")
                .Annotation("Npgsql:Enum:decision_vote_kind", "abstain,block,support")
                .Annotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .Annotation("Npgsql:Enum:expense_split_kind", "equal,exact,shares")
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

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "expires_at",
                table: "role_members",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "chores",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    interval_days = table.Column<int>(type: "integer", nullable: false),
                    anchor_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    effort_minutes = table.Column<int>(type: "integer", nullable: false),
                    rotation_role_id = table.Column<string>(type: "text", nullable: true),
                    fixed_assignee_user_id = table.Column<string>(type: "text", nullable: true),
                    grace_hours = table.Column<int>(type: "integer", nullable: false),
                    is_paused = table.Column<bool>(type: "boolean", nullable: false),
                    next_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chores", x => x.id);
                    table.ForeignKey(
                        name: "fk_chores_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "decisions",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    created_by_user_id = table.Column<string>(type: "text", nullable: false),
                    closes_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    quorum = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<DecisionStatus>(type: "decision_status", nullable: false),
                    outcome_option_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_decisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_decisions_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "expenses",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    payer_user_id = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    split_kind = table.Column<ExpenseSplitKind>(type: "expense_split_kind", nullable: false),
                    created_by_user_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_expenses", x => x.id);
                    table.ForeignKey(
                        name: "fk_expenses_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "guild_quiet_hours_configs",
                columns: table => new
                {
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    start_minute_local = table.Column<int>(type: "integer", nullable: false),
                    end_minute_local = table.Column<int>(type: "integer", nullable: false),
                    time_zone_id = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guild_quiet_hours_configs", x => x.guild_id);
                    table.ForeignKey(
                        name: "fk_guild_quiet_hours_configs_guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ledger_configs",
                columns: table => new
                {
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_configs", x => x.channel_id);
                    table.ForeignKey(
                        name: "fk_ledger_configs_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "list_items",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    quantity = table.Column<string>(type: "text", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    section = table.Column<string>(type: "text", nullable: true),
                    assignee_user_id = table.Column<string>(type: "text", nullable: true),
                    added_by_user_id = table.Column<string>(type: "text", nullable: false),
                    is_checked = table.Column<bool>(type: "boolean", nullable: false),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    checked_by_user_id = table.Column<string>(type: "text", nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false),
                    source_pantry_item_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_list_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_list_items_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pantry_configs",
                columns: table => new
                {
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    restock_list_channel_id = table.Column<string>(type: "text", nullable: true),
                    expiry_warning_days = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pantry_configs", x => x.channel_id);
                    table.ForeignKey(
                        name: "fk_pantry_configs_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pantry_items",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric", nullable: false),
                    unit = table.Column<string>(type: "text", nullable: true),
                    low_threshold = table.Column<decimal>(type: "numeric", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    restocked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    added_by_user_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pantry_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_pantry_items_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "settlements",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    from_user_id = table.Column<string>(type: "text", nullable: false),
                    to_user_id = table.Column<string>(type: "text", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    settled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_by_user_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_settlements", x => x.id);
                    table.ForeignKey(
                        name: "fk_settlements_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chore_occurrences",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    chore_id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    assigned_user_id = table.Column<string>(type: "text", nullable: false),
                    effort_minutes = table.Column<int>(type: "integer", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_by_user_id = table.Column<string>(type: "text", nullable: true),
                    skipped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chore_occurrences", x => x.id);
                    table.ForeignKey(
                        name: "fk_chore_occurrences_chores_chore_id",
                        column: x => x.chore_id,
                        principalTable: "chores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "decision_options",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    decision_id = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_decision_options", x => x.id);
                    table.ForeignKey(
                        name: "fk_decision_options_decisions_decision_id",
                        column: x => x.decision_id,
                        principalTable: "decisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "decision_votes",
                columns: table => new
                {
                    decision_id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    option_id = table.Column<string>(type: "text", nullable: true),
                    kind = table.Column<DecisionVoteKind>(type: "decision_vote_kind", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_decision_votes", x => new { x.decision_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_decision_votes_decisions_decision_id",
                        column: x => x.decision_id,
                        principalTable: "decisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "expense_shares",
                columns: table => new
                {
                    expense_id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    share_value = table.Column<decimal>(type: "numeric", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_expense_shares", x => new { x.expense_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_expense_shares_expenses_expense_id",
                        column: x => x.expense_id,
                        principalTable: "expenses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_role_members_expires_at",
                table: "role_members",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_chore_occurrences_channel_id_due_at",
                table: "chore_occurrences",
                columns: new[] { "channel_id", "due_at" });

            migrationBuilder.CreateIndex(
                name: "ix_chore_occurrences_chore_id_due_at",
                table: "chore_occurrences",
                columns: new[] { "chore_id", "due_at" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_chore_occurrences_guild_id_assigned_user_id_completed_at",
                table: "chore_occurrences",
                columns: new[] { "guild_id", "assigned_user_id", "completed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_chores_channel_id",
                table: "chores",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_chores_is_paused_next_due_at",
                table: "chores",
                columns: new[] { "is_paused", "next_due_at" });

            migrationBuilder.CreateIndex(
                name: "ix_decision_options_decision_id_position",
                table: "decision_options",
                columns: new[] { "decision_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_decisions_channel_id_status",
                table: "decisions",
                columns: new[] { "channel_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_decisions_status_closes_at",
                table: "decisions",
                columns: new[] { "status", "closes_at" });

            migrationBuilder.CreateIndex(
                name: "ix_expenses_channel_id_occurred_at",
                table: "expenses",
                columns: new[] { "channel_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_list_items_channel_id_is_checked_position",
                table: "list_items",
                columns: new[] { "channel_id", "is_checked", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_pantry_items_channel_id_name",
                table: "pantry_items",
                columns: new[] { "channel_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_pantry_items_guild_id_expires_at",
                table: "pantry_items",
                columns: new[] { "guild_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_settlements_channel_id_settled_at",
                table: "settlements",
                columns: new[] { "channel_id", "settled_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chore_occurrences");

            migrationBuilder.DropTable(
                name: "decision_options");

            migrationBuilder.DropTable(
                name: "decision_votes");

            migrationBuilder.DropTable(
                name: "expense_shares");

            migrationBuilder.DropTable(
                name: "guild_quiet_hours_configs");

            migrationBuilder.DropTable(
                name: "ledger_configs");

            migrationBuilder.DropTable(
                name: "list_items");

            migrationBuilder.DropTable(
                name: "pantry_configs");

            migrationBuilder.DropTable(
                name: "pantry_items");

            migrationBuilder.DropTable(
                name: "settlements");

            migrationBuilder.DropTable(
                name: "chores");

            migrationBuilder.DropTable(
                name: "decisions");

            migrationBuilder.DropTable(
                name: "expenses");

            migrationBuilder.DropIndex(
                name: "ix_role_members_expires_at",
                table: "role_members");

            migrationBuilder.DropColumn(
                name: "expires_at",
                table: "role_members");

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
                .OldAnnotation("Npgsql:Enum:channel_type", "announcement,chores,decisions,forum,ledger,list,media,pantry,text,thread,ticket,voice")
                .OldAnnotation("Npgsql:Enum:decision_status", "blocked,cancelled,decided,expired,open")
                .OldAnnotation("Npgsql:Enum:decision_vote_kind", "abstain,block,support")
                .OldAnnotation("Npgsql:Enum:encryption_state", "encrypted,encrypted_without_fallback_key,plain")
                .OldAnnotation("Npgsql:Enum:expense_split_kind", "equal,exact,shares")
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
