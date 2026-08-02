using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Messaging.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMlsProposalsAdmissionChallengesAndDeviceIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Three index swaps, no column drops - a rollback to the previous application version
            // still starts and serves traffic against this schema.
            //
            // The commit index is recreated below with `WHERE is_proposal = false`. A proposal does
            // not advance the group's epoch, so it must not own an epoch slot either: unfiltered,
            // a Remove proposal announced at epoch N+1 blocks the commit that actually establishes
            // N+1 and the group can never move again.
            migrationBuilder.DropIndex(
                name: "ix_mls_commits_context_id_generation_epoch",
                table: "mls_commits");

            // member_devices.device_id was *globally* unique, which meant a device could belong to
            // at most one conversation in the entire system. Nothing hit it only because nothing
            // writes these rows yet. Replaced with unique per (member, device) plus a plain lookup
            // index on device_id.
            migrationBuilder.DropIndex(
                name: "ix_member_devices_conversation_member_id",
                table: "member_devices");

            migrationBuilder.DropIndex(
                name: "ix_member_devices_device_id",
                table: "member_devices");

            migrationBuilder.AddColumn<bool>(
                name: "requires_manual_approval",
                table: "mls_join_requests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_proposal",
                table: "mls_commits",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "mls_admission_challenges",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    join_request_id = table.Column<string>(type: "text", nullable: false),
                    context_id = table.Column<string>(type: "text", nullable: false),
                    issued_by_user_id = table.Column<string>(type: "text", nullable: false),
                    issued_by_device_id = table.Column<string>(type: "text", nullable: true),
                    challenge = table.Column<byte[]>(type: "bytea", nullable: false),
                    proof = table.Column<byte[]>(type: "bytea", nullable: true),
                    proof_submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mls_admission_challenges", x => x.id);
                    table.ForeignKey(
                        name: "fk_mls_admission_challenges_mls_join_requests_join_request_id",
                        column: x => x.join_request_id,
                        principalTable: "mls_join_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mls_commits_context_id_generation_epoch",
                table: "mls_commits",
                columns: new[] { "context_id", "generation", "epoch" },
                unique: true,
                filter: "is_proposal = false");

            migrationBuilder.CreateIndex(
                name: "ix_member_devices_conversation_member_id_device_id",
                table: "member_devices",
                columns: new[] { "conversation_member_id", "device_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_member_devices_device_id",
                table: "member_devices",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_mls_admission_challenges_join_request_id_expires_at",
                table: "mls_admission_challenges",
                columns: new[] { "join_request_id", "expires_at" });
        }

        /// <inheritdoc />
        /// <summary>
        /// Reverses <see cref="Up"/>.
        ///
        /// <para><b>Two of the three index swaps cannot be undone without deleting rows, and the
        /// scaffolded code did not.</b> Up widened both indexes precisely because the data no longer
        /// fits the narrow shape, so recreating them as-was over live data raises 23505 and aborts the
        /// rollback halfway - the one moment a rollback has to work.</para>
        ///
        /// <list type="bullet">
        /// <item><c>mls_commits</c>: the old unique index had no <c>WHERE is_proposal = false</c>
        /// filter, so proposals - which by design share an epoch slot with the commit that establishes
        /// it - collide. They are deleted, which is correct rather than merely convenient: the
        /// application version being rolled back to has no concept of a proposal and would treat every
        /// one of these rows as a real commit.</item>
        /// <item><c>member_devices</c>: <c>device_id</c> was globally unique, meaning one device could
        /// belong to one conversation in the entire system. Any device in two conversations has to
        /// lose all but one membership row.</item>
        /// </list>
        ///
        /// <para>Both deletions run before the columns they read are dropped.</para>
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mls_admission_challenges");

            // While `is_proposal` still exists to select on.
            migrationBuilder.Sql("DELETE FROM mls_commits WHERE is_proposal = true;");

            // Keep one membership row per device. ctid is the tie-break: there is no meaningful
            // "right" one to keep once the schema says a device has exactly one conversation.
            migrationBuilder.Sql("""
                DELETE FROM member_devices a
                USING member_devices b
                WHERE a.device_id = b.device_id AND a.ctid < b.ctid;
                """);

            migrationBuilder.DropIndex(
                name: "ix_mls_commits_context_id_generation_epoch",
                table: "mls_commits");

            migrationBuilder.DropIndex(
                name: "ix_member_devices_conversation_member_id_device_id",
                table: "member_devices");

            migrationBuilder.DropIndex(
                name: "ix_member_devices_device_id",
                table: "member_devices");

            migrationBuilder.DropColumn(
                name: "requires_manual_approval",
                table: "mls_join_requests");

            migrationBuilder.DropColumn(
                name: "is_proposal",
                table: "mls_commits");

            migrationBuilder.CreateIndex(
                name: "ix_mls_commits_context_id_generation_epoch",
                table: "mls_commits",
                columns: new[] { "context_id", "generation", "epoch" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_member_devices_conversation_member_id",
                table: "member_devices",
                column: "conversation_member_id");

            migrationBuilder.CreateIndex(
                name: "ix_member_devices_device_id",
                table: "member_devices",
                column: "device_id",
                unique: true);
        }
    }
}
