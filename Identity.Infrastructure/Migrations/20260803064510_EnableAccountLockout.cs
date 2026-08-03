using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Migrations
{
    /// <summary>
    /// Backfills lockout_enabled for accounts created before ApplicationUser.Create/CreateBot began
    /// setting it.
    /// </summary>
    public partial class EnableAccountLockout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE asp_net_users SET lockout_enabled = TRUE WHERE lockout_enabled = FALSE;");

            // Counters accumulated while the control was inert must not suddenly take effect and
            // lock out real users the moment the flag goes live.
            migrationBuilder.Sql(
                "UPDATE asp_net_users SET lockout_end = NULL, access_failed_count = 0 " +
                "WHERE lockout_end IS NOT NULL OR access_failed_count <> 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE asp_net_users SET lockout_enabled = FALSE;");
        }
    }
}
