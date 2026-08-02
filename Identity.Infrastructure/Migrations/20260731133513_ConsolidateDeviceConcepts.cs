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
    ///
    /// <para>Hand-edited from the scaffolded version, which dropped both source tables before
    /// creating the destination - i.e. deleted every push token in production. The order here is
    /// create, copy, then drop.</para>
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

            // Carry both old tables over before they go. Ids are kept as-is rather than reissued
            // with the new "uptk" prefix: they are opaque, and the two source prefixes (udtk,
            // uvtk) can't collide with each other. Tokens land unattached (device_id null) - there
            // was never anything recording which installation registered them, and inventing a
            // link would be guesswork. Clients re-register on next launch and get attributed then.
            //
            // DISTINCT ON dedupes within each source: the old endpoints only checked "does this
            // user already have this token", so the same token could exist against several users
            // after a handset changed hands, which the new unique (kind, token) index forbids. The
            // most recently updated row wins - that is the account the handset actually belongs to.
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

        /// <summary>
        /// Reverses <see cref="Up"/>.
        ///
        /// <para><b>One thing cannot be reversed, and the scaffolded code failed rather than saying
        /// so.</b> Up replaced the globally unique <c>ix_user_devices_client_device_id</c> with a
        /// unique index over <c>(user_id, client_device_id)</c>, because that id is chosen by the
        /// client and is only ever unique per user. From that moment two accounts may legitimately
        /// hold a device row with the same <c>client_device_id</c> - a common one at that, since the
        /// value is often a stable per-handset string and handsets get signed into more than one
        /// account. The generated Down recreated the old index as <c>unique</c> over exactly that
        /// data, so on any database where it had happened the rollback raised 23505 on its final
        /// statement and rolled itself back: the escape hatch was missing precisely on the databases
        /// that had been used.</para>
        ///
        /// <para><b>The index is therefore restored non-unique.</b> The two alternatives are worse.
        /// Failing is what the bug already did. Deleting the colliding rows would delete a
        /// <i>different user's</i> device - cascading to their push tokens and their backup blobs, and
        /// handing the surviving account a device row it does not own, which is the C2 outcome
        /// arriving by migration. Note the contrast with
        /// <c>AddMlsIdentityKeysBackupAndProtectionLevel</c>, whose Down does delete: there the
        /// redundant rows are superseded versions of one device's own blob, which is history, not
        /// somebody else's data.</para>
        ///
        /// <para>Nothing downstream needs the uniqueness: the pre-migration code read
        /// <c>client_device_id</c> through this index but never relied on the database to keep it
        /// singular, and re-running <see cref="Up"/> drops the index by name regardless of its kind.
        /// The collisions are reported as warnings rather than passed over in silence, because after
        /// a rollback the older code resolves such an id to an arbitrary one of the two rows and an
        /// operator has to know that before it matters.</para>
        /// </summary>
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
            // The device link has no home in the old shape and is dropped.
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
            // not silent about the one guarantee it cannot give back. A WARNING rather than an
            // exception: the operator needs to know, and still needs the rollback to finish.
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
