using System;
using Domain;
using Identity.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserPrivacySettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:age_vertification_level", "ai_estimation,goverment_id,none,self_declaration")
                .Annotation("Npgsql:Enum:device_status", "active,removed")
                .Annotation("Npgsql:Enum:device_type", "desktop,mobile,web")
                .Annotation("Npgsql:Enum:direct_message_policy", "everyone,friends,friends_and_server_members,nobody")
                .Annotation("Npgsql:Enum:direct_message_settings", "allow_all,filter_all,filter_non_friends")
                .Annotation("Npgsql:Enum:explicit_content_filter", "everyone,off,unknown_senders")
                .Annotation("Npgsql:Enum:friend_request_policy", "everyone,friends_of_friends,nobody,server_members")
                .Annotation("Npgsql:Enum:privacy_settings", "allow_data_collection,allow_data_use_for_personalization,allow_voice_recorded_in_clips,none")
                .Annotation("Npgsql:Enum:push_token_kind", "apns_voip,fcm")
                .Annotation("Npgsql:Enum:theme", "dark,light,midnight,system")
                .Annotation("Npgsql:Enum:user_status", "active,banned,deleted,inactive,pending_deletion,purge_in_progress")
                .Annotation("Npgsql:Enum:user_type", "admin,bot,default,moderator")
                .Annotation("Npgsql:Enum:visibility", "everyone,friends,nobody")
                .OldAnnotation("Npgsql:Enum:age_vertification_level", "ai_estimation,goverment_id,none,self_declaration")
                .OldAnnotation("Npgsql:Enum:device_status", "active,removed")
                .OldAnnotation("Npgsql:Enum:device_type", "desktop,mobile,web")
                .OldAnnotation("Npgsql:Enum:direct_message_settings", "allow_all,filter_all,filter_non_friends")
                .OldAnnotation("Npgsql:Enum:privacy_settings", "allow_data_collection,allow_data_use_for_personalization,allow_voice_recorded_in_clips,none")
                .OldAnnotation("Npgsql:Enum:push_token_kind", "apns_voip,fcm")
                .OldAnnotation("Npgsql:Enum:theme", "dark,light,midnight,system")
                .OldAnnotation("Npgsql:Enum:user_status", "active,banned,deleted,inactive,pending_deletion,purge_in_progress")
                .OldAnnotation("Npgsql:Enum:user_type", "admin,bot,default,moderator");

            migrationBuilder.CreateTable(
                name: "user_privacy_settings",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    allow_data_collection = table.Column<bool>(type: "boolean", nullable: false),
                    allow_personalization = table.Column<bool>(type: "boolean", nullable: false),
                    allow_voice_recording_in_clips = table.Column<bool>(type: "boolean", nullable: false),
                    direct_message_policy = table.Column<DirectMessagePolicy>(type: "direct_message_policy", nullable: false),
                    friend_request_policy = table.Column<FriendRequestPolicy>(type: "friend_request_policy", nullable: false),
                    discoverable_by_username = table.Column<bool>(type: "boolean", nullable: false),
                    discoverable_by_email = table.Column<bool>(type: "boolean", nullable: false),
                    discoverable_by_phone = table.Column<bool>(type: "boolean", nullable: false),
                    mutual_servers_visibility = table.Column<Visibility>(type: "visibility", nullable: false),
                    mutual_friends_visibility = table.Column<Visibility>(type: "visibility", nullable: false),
                    connections_visibility = table.Column<Visibility>(type: "visibility", nullable: false),
                    birthday_visibility = table.Column<Visibility>(type: "visibility", nullable: false),
                    share_activity = table.Column<bool>(type: "boolean", nullable: false),
                    allow_positional_voice_capture = table.Column<bool>(type: "boolean", nullable: false),
                    send_read_receipts = table.Column<bool>(type: "boolean", nullable: false),
                    send_typing_indicators = table.Column<bool>(type: "boolean", nullable: false),
                    dm_retention_days = table.Column<int>(type: "integer", nullable: true),
                    explicit_content_filter = table.Column<ExplicitContentFilter>(type: "explicit_content_filter", nullable: false),
                    hide_push_content = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_privacy_settings", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_privacy_settings_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_privacy_settings_user_id",
                table: "user_privacy_settings",
                column: "user_id",
                unique: true);

            // ──────────────────────────────────────────────────────────────────────────────
            // Backfill.
            migrationBuilder.Sql("""
                INSERT INTO user_privacy_settings (
                    id, user_id,
                    allow_data_collection, allow_personalization, allow_voice_recording_in_clips,
                    direct_message_policy, friend_request_policy,
                    discoverable_by_username, discoverable_by_email, discoverable_by_phone,
                    mutual_servers_visibility, mutual_friends_visibility, connections_visibility,
                    birthday_visibility,
                    share_activity, allow_positional_voice_capture,
                    send_read_receipts, send_typing_indicators, dm_retention_days,
                    explicit_content_filter, hide_push_content,
                    version, created_at, updated_at)
                SELECT
                    -- prefix_<26 Crockford base32 chars>, matching Ids.Identifier: 10 characters of
                    -- millisecond timestamp followed by 16 of randomness. Not a true ULID (the
                    -- random half is derived from md5, whose uppercased hex digits happen to all be
                    -- valid Crockford characters), but the same length, the same alphabet and the
                    -- same sort-by-mint-time property.
                    'upvs_' || minted.body
                             || upper(substr(md5(random()::text || clock_timestamp()::text || u.id), 1, 16)),
                    u.id,

                    -- The three consent flags. Absent user_preferences means absent consent, which
                    -- is false - never true.
                    COALESCE(p.privacy_settings::text LIKE '%allow_data_collection%', false),
                    COALESCE(p.privacy_settings::text LIKE '%allow_data_use_for_personalization%', false),
                    COALESCE(p.privacy_settings::text LIKE '%allow_voice_recorded_in_clips%', false),

                    -- FilterAll/FilterNonFriends/AllowAll named the filter; the new enum names the
                    -- people admitted. A missing or unrecognised legacy value lands on 'friends',
                    -- which is both the new default and the restrictive reading.
                    (CASE p.direct_message_settings::text
                        WHEN 'allow_all'          THEN 'everyone'
                        WHEN 'filter_non_friends' THEN 'friends'
                        WHEN 'filter_all'         THEN 'nobody'
                        ELSE 'friends'
                     END)::direct_message_policy,
                    'everyone'::friend_request_policy,

                    true, false, false,

                    'friends'::visibility, 'friends'::visibility, 'friends'::visibility,
                    'nobody'::visibility,

                    true, true,

                    true, true, NULL,

                    'unknown_senders'::explicit_content_filter, false,

                    0, now(), now()
                FROM asp_net_users u
                LEFT JOIN user_preferences p ON p.id = u.user_preferences_id
                CROSS JOIN (
                    SELECT string_agg(
                        substr('0123456789ABCDEFGHJKMNPQRSTVWXYZ',
                               ((((EXTRACT(EPOCH FROM now()) * 1000)::bigint >> (5 * (9 - g))) & 31)::int) + 1,
                               1),
                        '' ORDER BY g) AS body
                    FROM generate_series(0, 9) AS g
                ) minted
                WHERE NOT EXISTS (
                    SELECT 1 FROM user_privacy_settings s WHERE s.user_id = u.id
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping the table discards every privacy choice made since the Up ran.
            migrationBuilder.DropTable(
                name: "user_privacy_settings");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:age_vertification_level", "ai_estimation,goverment_id,none,self_declaration")
                .Annotation("Npgsql:Enum:device_status", "active,removed")
                .Annotation("Npgsql:Enum:device_type", "desktop,mobile,web")
                .Annotation("Npgsql:Enum:direct_message_settings", "allow_all,filter_all,filter_non_friends")
                .Annotation("Npgsql:Enum:privacy_settings", "allow_data_collection,allow_data_use_for_personalization,allow_voice_recorded_in_clips,none")
                .Annotation("Npgsql:Enum:push_token_kind", "apns_voip,fcm")
                .Annotation("Npgsql:Enum:theme", "dark,light,midnight,system")
                .Annotation("Npgsql:Enum:user_status", "active,banned,deleted,inactive,pending_deletion,purge_in_progress")
                .Annotation("Npgsql:Enum:user_type", "admin,bot,default,moderator")
                .OldAnnotation("Npgsql:Enum:age_vertification_level", "ai_estimation,goverment_id,none,self_declaration")
                .OldAnnotation("Npgsql:Enum:device_status", "active,removed")
                .OldAnnotation("Npgsql:Enum:device_type", "desktop,mobile,web")
                .OldAnnotation("Npgsql:Enum:direct_message_policy", "everyone,friends,friends_and_server_members,nobody")
                .OldAnnotation("Npgsql:Enum:direct_message_settings", "allow_all,filter_all,filter_non_friends")
                .OldAnnotation("Npgsql:Enum:explicit_content_filter", "everyone,off,unknown_senders")
                .OldAnnotation("Npgsql:Enum:friend_request_policy", "everyone,friends_of_friends,nobody,server_members")
                .OldAnnotation("Npgsql:Enum:privacy_settings", "allow_data_collection,allow_data_use_for_personalization,allow_voice_recorded_in_clips,none")
                .OldAnnotation("Npgsql:Enum:push_token_kind", "apns_voip,fcm")
                .OldAnnotation("Npgsql:Enum:theme", "dark,light,midnight,system")
                .OldAnnotation("Npgsql:Enum:user_status", "active,banned,deleted,inactive,pending_deletion,purge_in_progress")
                .OldAnnotation("Npgsql:Enum:user_type", "admin,bot,default,moderator")
                .OldAnnotation("Npgsql:Enum:visibility", "everyone,friends,nobody");
        }
    }
}
