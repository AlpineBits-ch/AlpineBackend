using System;
using Identity.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Migrations
{
    /// <summary>
    /// Folds user_device_tokens (FCM) and user_voip_tokens (APNs VoIP) into a single
    /// user_push_tokens table with a kind column and an optional device link, links login_sessions
    /// to the device they came from, and scopes the client_device_id uniqueness to the owning user.
    /// </summary>
    public partial class ConsolidateDeviceConcepts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_user_devices_client_device_id",
                table: "user_devices");

            migrationBuilder.DropIndex(
                name: "ix_user_devices_user_id",
                table: "user_devices");

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
                .OldAnnotation("Npgsql:Enum:direct_message_settings", "allow_all,filter_all,filter_non_friends")
                .OldAnnotation("Npgsql:Enum:privacy_settings", "allow_data_collection,allow_data_use_for_personalization,allow_voice_recorded_in_clips,none")
                .OldAnnotation("Npgsql:Enum:theme", "dark,light,midnight,system")
                .OldAnnotation("Npgsql:Enum:user_status", "active,banned,deleted,inactive,pending_deletion,purge_in_progress")
                .OldAnnotation("Npgsql:Enum:user_type", "admin,bot,default,moderator");

            migrationBuilder.AddColumn<string>(
                name: "device_id",
                table: "login_sessions",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "user_push_tokens",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    token = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<PushTokenKind>(type: "push_token_kind", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    device_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_push_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_push_tokens_user_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "user_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_push_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Carry both old tables over before they go.
            migrationBuilder.Sql("""
                INSERT INTO user_push_tokens (id, token, kind, user_id, device_id, created_at, updated_at)
                SELECT DISTINCT ON (t.token) t.id, t.token, 'fcm'::push_token_kind, t.user_id, NULL, t.created_at, t.updated_at
                FROM user_device_tokens t
                ORDER BY t.token, t.updated_at DESC;
                """);

            migrationBuilder.Sql("""
                INSERT INTO user_push_tokens (id, token, kind, user_id, device_id, created_at, updated_at)
                SELECT DISTINCT ON (t.token) t.id, t.token, 'apns_voip'::push_token_kind, t.user_id, NULL, t.created_at, t.updated_at
                FROM user_voip_tokens t
                ORDER BY t.token, t.updated_at DESC;
                """);

            migrationBuilder.DropTable(
                name: "user_device_tokens");

            migrationBuilder.DropTable(
                name: "user_voip_tokens");

            migrationBuilder.CreateIndex(
                name: "ix_user_devices_user_id_client_device_id",
                table: "user_devices",
                columns: new[] { "user_id", "client_device_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_login_sessions_device_id",
                table: "login_sessions",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_push_tokens_device_id",
                table: "user_push_tokens",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_push_tokens_kind_token",
                table: "user_push_tokens",
                columns: new[] { "kind", "token" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_push_tokens_user_id",
                table: "user_push_tokens",
                column: "user_id");

            migrationBuilder.AddForeignKey(
                name: "fk_login_sessions_user_devices_device_id",
                table: "login_sessions",
                column: "device_id",
                principalTable: "user_devices",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <summary>Reverses <see cref="Up"/>.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_login_sessions_user_devices_device_id",
                table: "login_sessions");

            migrationBuilder.DropIndex(
                name: "ix_user_devices_user_id_client_device_id",
                table: "user_devices");

            migrationBuilder.DropIndex(
                name: "ix_login_sessions_device_id",
                table: "login_sessions");

            migrationBuilder.DropColumn(
                name: "device_id",
                table: "login_sessions");

            migrationBuilder.CreateTable(
                name: "user_device_tokens",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    token = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_device_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_device_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_voip_tokens",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    token = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_voip_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_voip_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Split the merged table back out before it goes, so a rollback keeps delivering push.
            migrationBuilder.Sql("""
                INSERT INTO user_device_tokens (id, token, user_id, created_at, updated_at)
                SELECT id, token, user_id, created_at, updated_at
                FROM user_push_tokens WHERE kind = 'fcm'::push_token_kind;
                """);

            migrationBuilder.Sql("""
                INSERT INTO user_voip_tokens (id, token, user_id, created_at, updated_at)
                SELECT id, token, user_id, created_at, updated_at
                FROM user_push_tokens WHERE kind = 'apns_voip'::push_token_kind;
                """);

            migrationBuilder.DropTable(
                name: "user_push_tokens");

            // Only now can the enum type go - it is dropped by this call, and both the table above
            // and the casts in the copy-back reference it.
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:age_vertification_level", "ai_estimation,goverment_id,none,self_declaration")
                .Annotation("Npgsql:Enum:device_status", "active,removed")
                .Annotation("Npgsql:Enum:device_type", "desktop,mobile,web")
                .Annotation("Npgsql:Enum:direct_message_settings", "allow_all,filter_all,filter_non_friends")
                .Annotation("Npgsql:Enum:privacy_settings", "allow_data_collection,allow_data_use_for_personalization,allow_voice_recorded_in_clips,none")
                .Annotation("Npgsql:Enum:theme", "dark,light,midnight,system")
                .Annotation("Npgsql:Enum:user_status", "active,banned,deleted,inactive,pending_deletion,purge_in_progress")
                .Annotation("Npgsql:Enum:user_type", "admin,bot,default,moderator")
                .OldAnnotation("Npgsql:Enum:age_vertification_level", "ai_estimation,goverment_id,none,self_declaration")
                .OldAnnotation("Npgsql:Enum:device_status", "active,removed")
                .OldAnnotation("Npgsql:Enum:device_type", "desktop,mobile,web")
                .OldAnnotation("Npgsql:Enum:direct_message_settings", "allow_all,filter_all,filter_non_friends")
                .OldAnnotation("Npgsql:Enum:privacy_settings", "allow_data_collection,allow_data_use_for_personalization,allow_voice_recorded_in_clips,none")
                .OldAnnotation("Npgsql:Enum:push_token_kind", "apns_voip,fcm")
                .OldAnnotation("Npgsql:Enum:theme", "dark,light,midnight,system")
                .OldAnnotation("Npgsql:Enum:user_status", "active,banned,deleted,inactive,pending_deletion,purge_in_progress")
                .OldAnnotation("Npgsql:Enum:user_type", "admin,bot,default,moderator");

            // Name every id that the old global uniqueness would have rejected, so the rollback is
            // not silent about the one guarantee it cannot give back.
            migrationBuilder.Sql("""
                DO $$
                DECLARE colliding text;
                BEGIN
                    SELECT string_agg(d.client_device_id, ', ')
                    INTO colliding
                    FROM (
                        SELECT client_device_id
                        FROM user_devices
                        GROUP BY client_device_id
                        HAVING count(*) > 1
                    ) d;

                    IF colliding IS NOT NULL THEN
                        RAISE WARNING 'ix_user_devices_client_device_id is being restored NON-UNIQUE: % is held by more than one account. Global uniqueness of client_device_id cannot be re-established without deleting another user''s device row.', colliding;
                    END IF;
                END $$;
                """);

            // Deliberately not unique - see the remarks on this method.
            migrationBuilder.CreateIndex(
                name: "ix_user_devices_client_device_id",
                table: "user_devices",
                column: "client_device_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_devices_user_id",
                table: "user_devices",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_device_tokens_user_id",
                table: "user_device_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_voip_tokens_user_id",
                table: "user_voip_tokens",
                column: "user_id");
        }
    }
}
