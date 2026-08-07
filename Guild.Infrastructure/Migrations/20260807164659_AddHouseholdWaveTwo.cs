using System;
using Guild.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHouseholdWaveTwo : Migration
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
                .OldAnnotation("Npgsql:Enum:audit_action_type", "auto_mod_config_updated,auto_mod_message_blocked,bot_installed,bot_uninstalled,category_created,category_deleted,category_updated,channel_created,channel_deleted,channel_follow_created,channel_follow_removed,channel_permission_changed,channel_updated,emoji_created,emoji_deleted,expense_created,expense_deleted,expense_updated,forum_config_updated,forum_tag_created,forum_tag_deleted,forum_tag_updated,forum_tags_reordered,guild_created_from_template,guild_deleted,guild_imported_from_discord,guild_synced_from_discord,guild_updated,invite_created,invite_deleted,ledger_config_updated,member_banned,member_kicked,member_left,member_moved_out,member_muted,member_nickname_changed,member_unbanned,member_unmuted,message_pinned,message_unpinned,onboarding_config_updated,onboarding_prompt_created,onboarding_prompt_deleted,onboarding_prompt_updated,role_created,role_deleted,role_positions_changed,role_updated,scheduled_event_cancelled,scheduled_event_created,scheduled_event_deleted,scheduled_event_updated,settlement_recorded,template_created,thread_lock_changed,thread_pin_changed,thread_tags_updated,welcome_screen_updated")
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

            migrationBuilder.AddColumn<string>(
                name: "barcode",
                table: "pantry_items",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bill_occurrence_id",
                table: "expenses",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<ExpenseCategory>(
                name: "category",
                table: "expenses",
                type: "expense_category",
                nullable: false,
                defaultValue: ExpenseCategory.Uncategorized);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "nudged_at",
                table: "chore_occurrences",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "expense_receipts",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    expense_id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    storage_key = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    uploaded_by_user_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_expense_receipts", x => x.id);
                    table.ForeignKey(
                        name: "fk_expense_receipts_expenses_expense_id",
                        column: x => x.expense_id,
                        principalTable: "expenses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "maintenance_assets",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    location = table.Column<string>(type: "text", nullable: true),
                    brand = table.Column<string>(type: "text", nullable: true),
                    model = table.Column<string>(type: "text", nullable: true),
                    serial_number = table.Column<string>(type: "text", nullable: true),
                    purchased_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    warranty_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    vendor_name = table.Column<string>(type: "text", nullable: true),
                    vendor_phone = table.Column<string>(type: "text", nullable: true),
                    vendor_email = table.Column<string>(type: "text", nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    service_interval_days = table.Column<int>(type: "integer", nullable: true),
                    last_serviced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_service_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<AssetStatus>(type: "asset_status", nullable: false),
                    status_note = table.Column<string>(type: "text", nullable: true),
                    service_notified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    warranty_notified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    added_by_user_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_maintenance_assets", x => x.id);
                    table.ForeignKey(
                        name: "fk_maintenance_assets_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "meal_plan_configs",
                columns: table => new
                {
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    shopping_list_channel_id = table.Column<string>(type: "text", nullable: true),
                    pantry_channel_id = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_meal_plan_configs", x => x.channel_id);
                    table.ForeignKey(
                        name: "fk_meal_plan_configs_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "member_absences",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    start_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_by_user_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_member_absences", x => x.id);
                    table.ForeignKey(
                        name: "fk_member_absences_guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pantry_barcodes",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    barcode = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    unit = table.Column<string>(type: "text", nullable: true),
                    default_quantity = table.Column<decimal>(type: "numeric", nullable: false),
                    low_threshold = table.Column<decimal>(type: "numeric", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    times_seen = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pantry_barcodes", x => x.id);
                    table.ForeignKey(
                        name: "fk_pantry_barcodes_guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payment_handle_blobs",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    member_roster_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_handle_blobs", x => x.id);
                    table.ForeignKey(
                        name: "fk_payment_handle_blobs_guilds_guild_id",
                        column: x => x.guild_id,
                        principalTable: "guilds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "recipes",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    servings = table.Column<int>(type: "integer", nullable: false),
                    prep_minutes = table.Column<int>(type: "integer", nullable: true),
                    source_url = table.Column<string>(type: "text", nullable: true),
                    created_by_user_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recipes", x => x.id);
                    table.ForeignKey(
                        name: "fk_recipes_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "recurring_expenses",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: true),
                    payer_user_id = table.Column<string>(type: "text", nullable: false),
                    split_kind = table.Column<ExpenseSplitKind>(type: "expense_split_kind", nullable: false),
                    category = table.Column<ExpenseCategory>(type: "expense_category", nullable: false),
                    recurrence_unit = table.Column<RecurrenceUnit>(type: "recurrence_unit", nullable: false),
                    recurrence_interval = table.Column<int>(type: "integer", nullable: false),
                    anchor_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lead_days = table.Column<int>(type: "integer", nullable: false),
                    auto_post = table.Column<bool>(type: "boolean", nullable: false),
                    is_paused = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_user_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recurring_expenses", x => x.id);
                    table.ForeignKey(
                        name: "fk_recurring_expenses_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "maintenance_records",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    asset_id = table.Column<string>(type: "text", nullable: true),
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    performed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    performed_by_user_id = table.Column<string>(type: "text", nullable: false),
                    vendor_name = table.Column<string>(type: "text", nullable: true),
                    cost_minor = table.Column<long>(type: "bigint", nullable: true),
                    currency = table.Column<string>(type: "text", nullable: true),
                    expense_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_maintenance_records", x => x.id);
                    table.ForeignKey(
                        name: "fk_maintenance_records_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_maintenance_records_maintenance_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "maintenance_assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "payment_handle_key_wraps",
                columns: table => new
                {
                    payment_handle_blob_id = table.Column<string>(type: "text", nullable: false),
                    recipient_device_id = table.Column<string>(type: "text", nullable: false),
                    recipient_user_id = table.Column<string>(type: "text", nullable: false),
                    wrapped_key = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_handle_key_wraps", x => new { x.payment_handle_blob_id, x.recipient_device_id });
                    table.ForeignKey(
                        name: "fk_payment_handle_key_wraps_payment_handle_blobs_payment_handl",
                        column: x => x.payment_handle_blob_id,
                        principalTable: "payment_handle_blobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "meal_plan_entries",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    slot = table.Column<MealSlot>(type: "meal_slot", nullable: false),
                    recipe_id = table.Column<string>(type: "text", nullable: true),
                    free_text = table.Column<string>(type: "text", nullable: true),
                    cook_user_id = table.Column<string>(type: "text", nullable: true),
                    servings = table.Column<int>(type: "integer", nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false),
                    created_by_user_id = table.Column<string>(type: "text", nullable: false),
                    notified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_meal_plan_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_meal_plan_entries_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_meal_plan_entries_recipes_recipe_id",
                        column: x => x.recipe_id,
                        principalTable: "recipes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "recipe_ingredients",
                columns: table => new
                {
                    recipe_id = table.Column<string>(type: "text", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    match_name = table.Column<string>(type: "text", nullable: true),
                    is_optional = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recipe_ingredients", x => new { x.recipe_id, x.position });
                    table.ForeignKey(
                        name: "fk_recipe_ingredients_recipes_recipe_id",
                        column: x => x.recipe_id,
                        principalTable: "recipes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "bill_occurrences",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    recurring_expense_id = table.Column<string>(type: "text", nullable: false),
                    guild_id = table.Column<string>(type: "text", nullable: false),
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    amount_minor = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<BillStatus>(type: "bill_status", nullable: false),
                    expense_id = table.Column<string>(type: "text", nullable: true),
                    posted_by_user_id = table.Column<string>(type: "text", nullable: true),
                    skipped_by_user_id = table.Column<string>(type: "text", nullable: true),
                    skip_reason = table.Column<string>(type: "text", nullable: true),
                    description = table.Column<string>(type: "text", nullable: false),
                    reminded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bill_occurrences", x => x.id);
                    table.ForeignKey(
                        name: "fk_bill_occurrences_recurring_expenses_recurring_expense_id",
                        column: x => x.recurring_expense_id,
                        principalTable: "recurring_expenses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "recurring_expense_shares",
                columns: table => new
                {
                    recurring_expense_id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    share_value = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recurring_expense_shares", x => new { x.recurring_expense_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_recurring_expense_shares_recurring_expenses_recurring_expen",
                        column: x => x.recurring_expense_id,
                        principalTable: "recurring_expenses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_pantry_items_channel_id_barcode",
                table: "pantry_items",
                columns: new[] { "channel_id", "barcode" },
                filter: "barcode IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_bill_occurrences_channel_id_due_at",
                table: "bill_occurrences",
                columns: new[] { "channel_id", "due_at" });

            migrationBuilder.CreateIndex(
                name: "ix_bill_occurrences_due_at",
                table: "bill_occurrences",
                column: "due_at",
                filter: "reminded_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_bill_occurrences_recurring_expense_id_due_at",
                table: "bill_occurrences",
                columns: new[] { "recurring_expense_id", "due_at" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_expense_receipts_expense_id",
                table: "expense_receipts",
                column: "expense_id");

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_assets_channel_id_name",
                table: "maintenance_assets",
                columns: new[] { "channel_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_assets_guild_id_status",
                table: "maintenance_assets",
                columns: new[] { "guild_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_assets_next_service_at",
                table: "maintenance_assets",
                column: "next_service_at",
                filter: "service_notified_at IS NULL AND next_service_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_assets_warranty_until",
                table: "maintenance_assets",
                column: "warranty_until",
                filter: "warranty_notified_at IS NULL AND warranty_until IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_records_asset_id_performed_at",
                table: "maintenance_records",
                columns: new[] { "asset_id", "performed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_records_channel_id_performed_at",
                table: "maintenance_records",
                columns: new[] { "channel_id", "performed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_meal_plan_entries_channel_id_date_slot_position",
                table: "meal_plan_entries",
                columns: new[] { "channel_id", "date", "slot", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_meal_plan_entries_date",
                table: "meal_plan_entries",
                column: "date",
                filter: "notified_at IS NULL AND cook_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_meal_plan_entries_recipe_id",
                table: "meal_plan_entries",
                column: "recipe_id");

            migrationBuilder.CreateIndex(
                name: "ix_member_absences_guild_id_start_at_end_at",
                table: "member_absences",
                columns: new[] { "guild_id", "start_at", "end_at" });

            migrationBuilder.CreateIndex(
                name: "ix_member_absences_guild_id_user_id_end_at",
                table: "member_absences",
                columns: new[] { "guild_id", "user_id", "end_at" });

            migrationBuilder.CreateIndex(
                name: "ix_pantry_barcodes_guild_id_barcode",
                table: "pantry_barcodes",
                columns: new[] { "guild_id", "barcode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pantry_barcodes_guild_id_times_seen",
                table: "pantry_barcodes",
                columns: new[] { "guild_id", "times_seen" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_handle_blobs_guild_id_user_id",
                table: "payment_handle_blobs",
                columns: new[] { "guild_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_handle_key_wraps_recipient_device_id",
                table: "payment_handle_key_wraps",
                column: "recipient_device_id");

            migrationBuilder.CreateIndex(
                name: "ix_recipes_channel_id_title",
                table: "recipes",
                columns: new[] { "channel_id", "title" });

            migrationBuilder.CreateIndex(
                name: "ix_recurring_expenses_channel_id",
                table: "recurring_expenses",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "ix_recurring_expenses_next_due_at",
                table: "recurring_expenses",
                column: "next_due_at",
                filter: "is_paused = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bill_occurrences");

            migrationBuilder.DropTable(
                name: "expense_receipts");

            migrationBuilder.DropTable(
                name: "maintenance_records");

            migrationBuilder.DropTable(
                name: "meal_plan_configs");

            migrationBuilder.DropTable(
                name: "meal_plan_entries");

            migrationBuilder.DropTable(
                name: "member_absences");

            migrationBuilder.DropTable(
                name: "pantry_barcodes");

            migrationBuilder.DropTable(
                name: "payment_handle_key_wraps");

            migrationBuilder.DropTable(
                name: "recipe_ingredients");

            migrationBuilder.DropTable(
                name: "recurring_expense_shares");

            migrationBuilder.DropTable(
                name: "maintenance_assets");

            migrationBuilder.DropTable(
                name: "payment_handle_blobs");

            migrationBuilder.DropTable(
                name: "recipes");

            migrationBuilder.DropTable(
                name: "recurring_expenses");

            migrationBuilder.DropIndex(
                name: "ix_pantry_items_channel_id_barcode",
                table: "pantry_items");

            migrationBuilder.DropColumn(
                name: "barcode",
                table: "pantry_items");

            migrationBuilder.DropColumn(
                name: "bill_occurrence_id",
                table: "expenses");

            migrationBuilder.DropColumn(
                name: "category",
                table: "expenses");

            migrationBuilder.DropColumn(
                name: "nudged_at",
                table: "chore_occurrences");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:audit_action_type", "auto_mod_config_updated,auto_mod_message_blocked,bot_installed,bot_uninstalled,category_created,category_deleted,category_updated,channel_created,channel_deleted,channel_follow_created,channel_follow_removed,channel_permission_changed,channel_updated,emoji_created,emoji_deleted,expense_created,expense_deleted,expense_updated,forum_config_updated,forum_tag_created,forum_tag_deleted,forum_tag_updated,forum_tags_reordered,guild_created_from_template,guild_deleted,guild_imported_from_discord,guild_synced_from_discord,guild_updated,invite_created,invite_deleted,ledger_config_updated,member_banned,member_kicked,member_left,member_moved_out,member_muted,member_nickname_changed,member_unbanned,member_unmuted,message_pinned,message_unpinned,onboarding_config_updated,onboarding_prompt_created,onboarding_prompt_deleted,onboarding_prompt_updated,role_created,role_deleted,role_positions_changed,role_updated,scheduled_event_cancelled,scheduled_event_created,scheduled_event_deleted,scheduled_event_updated,settlement_recorded,template_created,thread_lock_changed,thread_pin_changed,thread_tags_updated,welcome_screen_updated")
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
        }
    }
}
