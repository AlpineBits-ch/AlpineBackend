using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Billing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCreditLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "starts_at",
                table: "grants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "credit_campaigns",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    total_budget_points = table.Column<long>(type: "bigint", nullable: false),
                    issued_points = table.Column<long>(type: "bigint", nullable: false),
                    max_per_user_points = table.Column<long>(type: "bigint", nullable: true),
                    default_issue_points = table.Column<long>(type: "bigint", nullable: true),
                    lot_lifetime_days = table.Column<int>(type: "integer", nullable: true),
                    alert_threshold_percent = table.Column<int>(type: "integer", nullable: false),
                    alerted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    automated = table.Column<bool>(type: "boolean", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    paused_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    paused_by = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credit_campaigns", x => x.id);
                    table.CheckConstraint("ck_credit_campaigns_within_budget", "total_budget_points > 0 AND issued_points >= 0 AND issued_points <= total_budget_points");
                });

            migrationBuilder.CreateTable(
                name: "credit_entries",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    lot_id = table.Column<string>(type: "text", nullable: true),
                    campaign_id = table.Column<string>(type: "text", nullable: true),
                    grant_id = table.Column<string>(type: "text", nullable: true),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    created_by = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credit_entries", x => x.id);
                    table.CheckConstraint("ck_credit_entries_amount_sign", "(kind = 'Issue' AND amount > 0) OR (kind IN ('Spend', 'Expiry', 'Reversal') AND amount < 0) OR (kind = 'Adjustment' AND amount <> 0)");
                    table.CheckConstraint("ck_credit_entries_reason_required", "kind NOT IN ('Adjustment', 'Reversal') OR (reason IS NOT NULL AND btrim(reason) <> '')");
                });

            migrationBuilder.CreateTable(
                name: "credit_lots",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    original_amount = table.Column<long>(type: "bigint", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    campaign_id = table.Column<string>(type: "text", nullable: true),
                    expiry_warning_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credit_lots", x => x.id);
                    table.CheckConstraint("ck_credit_lots_amount_positive", "original_amount > 0");
                });

            migrationBuilder.CreateTable(
                name: "credit_wallets",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    cached_balance = table.Column<long>(type: "bigint", nullable: false),
                    cached_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credit_wallets", x => x.id);
                    table.CheckConstraint("ck_credit_wallets_balance_not_negative", "cached_balance >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_credit_campaigns_code",
                table: "credit_campaigns",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_credit_entries_campaign_id_user_id",
                table: "credit_entries",
                columns: new[] { "campaign_id", "user_id" },
                filter: "campaign_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_credit_entries_idempotency_key",
                table: "credit_entries",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_credit_entries_lot_id",
                table: "credit_entries",
                column: "lot_id");

            migrationBuilder.CreateIndex(
                name: "ix_credit_entries_user_id_created_at",
                table: "credit_entries",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_credit_lots_expires_at",
                table: "credit_lots",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_credit_lots_expires_at_expiry_warning_sent_at",
                table: "credit_lots",
                columns: new[] { "expires_at", "expiry_warning_sent_at" },
                filter: "expiry_warning_sent_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_credit_lots_user_id_expires_at",
                table: "credit_lots",
                columns: new[] { "user_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_credit_wallets_user_id",
                table: "credit_wallets",
                column: "user_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "credit_campaigns");

            migrationBuilder.DropTable(
                name: "credit_entries");

            migrationBuilder.DropTable(
                name: "credit_lots");

            migrationBuilder.DropTable(
                name: "credit_wallets");

            migrationBuilder.DropColumn(
                name: "starts_at",
                table: "grants");
        }
    }
}
