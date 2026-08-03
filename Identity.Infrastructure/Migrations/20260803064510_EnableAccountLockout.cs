using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Identity.Infrastructure.Migrations
{
    /// <summary>
    /// Backfills lockout_enabled for accounts created before ApplicationUser.Create/CreateBot began
    /// setting it.
    ///
    /// Users are persisted with ctx.Users.Add rather than UserManager.CreateAsync, and CreateAsync
    /// is the only place ASP.NET Identity would have set that flag - so every pre-existing row has
    /// it false. UserManager.IsLockedOutAsync returns false unconditionally when it is false, which
    /// made SignInManager's lockoutOnFailure a no-op: access_failed_count and lockout_end were
    /// being written but never honoured, and no account could actually lock. A code-only fix would
    /// leave every existing account permanently exempt.
    ///
    /// Data-only: no schema or model change, which is why the generated Up/Down were empty.
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
