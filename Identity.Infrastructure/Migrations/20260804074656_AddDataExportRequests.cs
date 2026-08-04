using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Migrations
{
    /// <summary>
    /// T1-7 of docs/specs/privacy.md - the GDPR Art. 15 / 20 access-and-portability path.
    ///
    /// <para><b>Purely additive.</b> One new table; nothing existing is altered, narrowed or dropped,
    /// and no database-level enum type is touched - <c>status</c> is a string column for the same
    /// reason the legal/consent/DSR enums are, so this migration cannot collide with a
    /// concurrently-authored one over the shared <c>AlterDatabase</c> annotation block. Follows
    /// <c>20260804070857_AddLegalConsentAndDsr</c> in the chain.</para>
    ///
    /// <para><b>The rollback loses the record that anybody ever exercised an access right</b> - when
    /// they asked, whether it was answered, and until when the answer was available. The archives
    /// themselves live in object storage and are untouched by a rollback, so dropping this table
    /// orphans any artifact that has not yet expired: nothing left in the database knows its key, and
    /// the expiry sweep can no longer find it. If this is ever rolled back with exports in flight, the
    /// bucket's <c>data-exports/</c> prefix has to be cleared by hand.</para>
    /// </summary>
    public partial class AddDataExportRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "data_export_requests",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    artifact_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_export_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_data_export_requests_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_data_export_requests_status_expires_at",
                table: "data_export_requests",
                columns: new[] { "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_data_export_requests_user_id_requested_at",
                table: "data_export_requests",
                columns: new[] { "user_id", "requested_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_export_requests");
        }
    }
}
