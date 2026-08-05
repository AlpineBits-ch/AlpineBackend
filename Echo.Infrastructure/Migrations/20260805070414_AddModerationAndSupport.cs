using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Echo.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddModerationAndSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "moderation_actions",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    target_user_id = table.Column<string>(type: "text", nullable: false),
                    actor_user_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    public_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    internal_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by_user_id = table.Column<string>(type: "text", nullable: true),
                    revocation_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    report_id = table.Column<string>(type: "text", nullable: true),
                    reference = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_moderation_actions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "moderation_audit_entries",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    actor_user_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    subject_id = table.Column<string>(type: "text", nullable: true),
                    detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_moderation_audit_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "moderation_reports",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    reporter_user_id = table.Column<string>(type: "text", nullable: true),
                    target_user_id = table.Column<string>(type: "text", nullable: false),
                    subject_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    subject_id = table.Column<string>(type: "text", nullable: true),
                    reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    details = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    evidence_json = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    priority = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    assigned_to_user_id = table.Column<string>(type: "text", nullable: true),
                    resolved_by_user_id = table.Column<string>(type: "text", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    duplicate_of_id = table.Column<string>(type: "text", nullable: true),
                    duplicate_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_moderation_reports", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "support_tickets",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    contact_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    requester_user_id = table.Column<string>(type: "text", nullable: true),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    assigned_to_user_id = table.Column<string>(type: "text", nullable: true),
                    reference = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    access_token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    last_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_support_tickets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "moderation_appeals",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    action_id = table.Column<string>(type: "text", nullable: false),
                    contact_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    submitted_by_user_id = table.Column<string>(type: "text", nullable: true),
                    body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    decided_by_user_id = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    reference = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_moderation_appeals", x => x.id);
                    table.ForeignKey(
                        name: "fk_moderation_appeals_moderation_actions_action_id",
                        column: x => x.action_id,
                        principalTable: "moderation_actions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "support_ticket_messages",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    ticket_id = table.Column<string>(type: "text", nullable: false),
                    author_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    author_user_id = table.Column<string>(type: "text", nullable: true),
                    body = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    is_internal = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_support_ticket_messages", x => x.id);
                    table.ForeignKey(
                        name: "fk_support_ticket_messages_support_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "support_tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_moderation_actions_actor_user_id",
                table: "moderation_actions",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_moderation_actions_reference",
                table: "moderation_actions",
                column: "reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_moderation_actions_target_user_id",
                table: "moderation_actions",
                column: "target_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_moderation_actions_target_user_id_kind_revoked_at_expires_at",
                table: "moderation_actions",
                columns: new[] { "target_user_id", "kind", "revoked_at", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_moderation_appeals_action_id",
                table: "moderation_appeals",
                column: "action_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_moderation_appeals_reference",
                table: "moderation_appeals",
                column: "reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_moderation_appeals_status_created_at",
                table: "moderation_appeals",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_moderation_audit_entries_actor_user_id_created_at",
                table: "moderation_audit_entries",
                columns: new[] { "actor_user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_moderation_audit_entries_subject_id_created_at",
                table: "moderation_audit_entries",
                columns: new[] { "subject_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_moderation_reports_assigned_to_user_id",
                table: "moderation_reports",
                column: "assigned_to_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_moderation_reports_reporter_user_id",
                table: "moderation_reports",
                column: "reporter_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_moderation_reports_reporter_user_id_subject_kind_subject_id",
                table: "moderation_reports",
                columns: new[] { "reporter_user_id", "subject_kind", "subject_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_moderation_reports_status_priority_created_at",
                table: "moderation_reports",
                columns: new[] { "status", "priority", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_moderation_reports_target_user_id",
                table: "moderation_reports",
                column: "target_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_support_ticket_messages_ticket_id_created_at",
                table: "support_ticket_messages",
                columns: new[] { "ticket_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_support_tickets_assigned_to_user_id",
                table: "support_tickets",
                column: "assigned_to_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_support_tickets_contact_email",
                table: "support_tickets",
                column: "contact_email");

            migrationBuilder.CreateIndex(
                name: "ix_support_tickets_reference",
                table: "support_tickets",
                column: "reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_support_tickets_status_last_activity_at",
                table: "support_tickets",
                columns: new[] { "status", "last_activity_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "moderation_appeals");

            migrationBuilder.DropTable(
                name: "moderation_audit_entries");

            migrationBuilder.DropTable(
                name: "moderation_reports");

            migrationBuilder.DropTable(
                name: "support_ticket_messages");

            migrationBuilder.DropTable(
                name: "moderation_actions");

            migrationBuilder.DropTable(
                name: "support_tickets");
        }
    }
}
