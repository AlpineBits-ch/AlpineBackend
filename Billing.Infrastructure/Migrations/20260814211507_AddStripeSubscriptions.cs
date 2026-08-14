using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Billing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStripeSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "stripe_product_id",
                table: "plans",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stripe_price_id",
                table: "plan_versions",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "processed_stripe_events",
                columns: table => new
                {
                    event_id = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processed_stripe_events", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "stripe_customers",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    stripe_customer_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stripe_customers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    stripe_subscription_id = table.Column<string>(type: "text", nullable: false),
                    payer_user_id = table.Column<string>(type: "text", nullable: false),
                    subject_kind = table.Column<string>(type: "text", nullable: false),
                    subject_id = table.Column<string>(type: "text", nullable: false),
                    plan_id = table.Column<string>(type: "text", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    current_period_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_at_period_end = table.Column<bool>(type: "boolean", nullable: false),
                    latest_invoice_id = table.Column<string>(type: "text", nullable: true),
                    grace_period_ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscriptions", x => x.id);
                    table.ForeignKey(
                        name: "fk_subscriptions_plans_plan_id",
                        column: x => x.plan_id,
                        principalTable: "plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stripe_customers_stripe_customer_id",
                table: "stripe_customers",
                column: "stripe_customer_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stripe_customers_user_id",
                table: "stripe_customers",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_plan_id",
                table: "subscriptions",
                column: "plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_stripe_subscription_id",
                table: "subscriptions",
                column: "stripe_subscription_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_subject_kind_subject_id",
                table: "subscriptions",
                columns: new[] { "subject_kind", "subject_id" },
                unique: true,
                filter: "status IN ('Trialing', 'Active', 'PastDue')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "processed_stripe_events");

            migrationBuilder.DropTable(
                name: "stripe_customers");

            migrationBuilder.DropTable(
                name: "subscriptions");

            migrationBuilder.DropColumn(
                name: "stripe_product_id",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "stripe_price_id",
                table: "plan_versions");
        }
    }
}
