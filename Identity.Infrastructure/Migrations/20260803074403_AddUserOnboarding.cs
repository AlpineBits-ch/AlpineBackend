using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Migrations
{
    /// <summary>
    /// Adds the onboarding answer to ApplicationUser: when the account said what it came for, and
    /// which halves of the product it picked.
    /// </summary>
    public partial class AddUserOnboarding : Migration
    {
        /// <summary>Isle | Social, matching Identity.Domain.Enums.UserInterests.</summary>
        private const int BothInterests = 3;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "interests",
                table: "asp_net_users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "onboarded_at",
                table: "asp_net_users",
                type: "timestamp with time zone",
                nullable: true);

            // See the remarks above: without this, every pre-existing account is walled behind the
            // onboarding picker on next launch.
            migrationBuilder.Sql(
                $"UPDATE asp_net_users SET onboarded_at = created_at, interests = {BothInterests} " +
                "WHERE onboarded_at IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "interests",
                table: "asp_net_users");

            migrationBuilder.DropColumn(
                name: "onboarded_at",
                table: "asp_net_users");
        }
    }
}
