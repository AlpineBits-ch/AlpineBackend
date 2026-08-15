using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Billing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPromotions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "promotion_campaigns",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    plan = table.Column<string>(type: "text", nullable: false),
                    trial_days = table.Column<int>(type: "integer", nullable: false),
                    subject_kind = table.Column<string>(type: "text", nullable: false),
                    total_budget_redemptions = table.Column<long>(type: "bigint", nullable: false),
                    issued_redemptions = table.Column<long>(type: "bigint", nullable: false),
                    max_per_subject = table.Column<int>(type: "integer", nullable: false),
                    required_signals = table.Column<int>(type: "integer", nullable: false),
                    minimum_account_age_days = table.Column<int>(type: "integer", nullable: false),
                    alert_threshold_percent = table.Column<int>(type: "integer", nullable: false),
                    alerted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
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
                    table.PrimaryKey("pk_promotion_campaigns", x => x.id);
                    table.CheckConstraint("ck_promotion_campaigns_within_budget", "total_budget_redemptions > 0 AND issued_redemptions >= 0 AND issued_redemptions <= total_budget_redemptions");
                });

            migrationBuilder.CreateTable(
                name: "promotion_redemptions",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    campaign_id = table.Column<string>(type: "text", nullable: false),
                    subject_kind = table.Column<string>(type: "text", nullable: false),
                    subject_id = table.Column<string>(type: "text", nullable: false),
                    owner_user_id = table.Column<string>(type: "text", nullable: false),
                    redeemed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    stripe_subscription_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_promotion_redemptions", x => x.id);
                    table.ForeignKey(
                        name: "fk_promotion_redemptions_promotion_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "promotion_campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "promotion_identity_marks",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    campaign_id = table.Column<string>(type: "text", nullable: false),
                    redemption_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_promotion_identity_marks", x => x.id);
                    table.ForeignKey(
                        name: "fk_promotion_identity_marks_promotion_redemptions_redemption_id",
                        column: x => x.redemption_id,
                        principalTable: "promotion_redemptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_promotion_campaigns_code",
                table: "promotion_campaigns",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_promotion_identity_marks_campaign_id_kind_hash",
                table: "promotion_identity_marks",
                columns: new[] { "campaign_id", "kind", "hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_promotion_identity_marks_redemption_id",
                table: "promotion_identity_marks",
                column: "redemption_id");

            migrationBuilder.CreateIndex(
                name: "ix_promotion_redemptions_campaign_id_subject_kind_subject_id",
                table: "promotion_redemptions",
                columns: new[] { "campaign_id", "subject_kind", "subject_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_promotion_redemptions_ends_at",
                table: "promotion_redemptions",
                column: "ends_at",
                filter: "ends_at IS NOT NULL AND expired_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_promotion_redemptions_owner_user_id_redeemed_at",
                table: "promotion_redemptions",
                columns: new[] { "owner_user_id", "redeemed_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "promotion_identity_marks");

            migrationBuilder.DropTable(
                name: "promotion_redemptions");

            migrationBuilder.DropTable(
                name: "promotion_campaigns");
        }
    }
}
