using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Migrations
{
    /// <summary>
    /// Adds the onboarding answer to ApplicationUser: when the account said what it came for, and
    /// which halves of the product it picked.
    ///
    /// <para><b>The backfill is the point of this migration, not the columns.</b> The client shows
    /// its onboarding picker on exactly one condition - onboarded_at being null - and reads a null
    /// as "has never answered". Adding the column without filling it hands every existing account a
    /// null, and every one of them meets a full-screen, unskippable picker on next launch. No
    /// client-side guard can help: "never onboarded" and "onboarded before this column existed"
    /// are identical by construction.
    ///
    /// <para>Existing accounts are stamped with their own created_at rather than now, that being
    /// the closest true answer to when they joined, and given both interests: they signed up when
    /// there was no choice to make and have had the run of the whole product since. Narrowing them
    /// to one half would silently revoke something nobody asked to give up - and for an account
    /// without a master key, it would stop the client ever asking for one.</para>
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
