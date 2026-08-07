using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <summary>
    /// Pure data migration - EF scaffolded it empty and it stays that way apart from the SQL below.
    /// </summary>
    public partial class BackfillHouseholdWaveTwoEveryonePermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Two more bits joined Role.HouseholdEveryonePermissions with the Meals and Maintenance
            // modules.
            migrationBuilder.Sql("""
                -- PlanMeals (2^55). Deciding what the house eats on Thursday is not a moderator
                -- action; ManageMeals, which edits somebody else's recipe, stays with Flatmates.
                UPDATE roles SET permissions = permissions + 36028797018963968
                WHERE (floor(permissions / 36028797018963968) % 2) = 0 AND type = 'everyone';

                -- LogMaintenance (2^57). The person who discovers the washing machine is dead is
                -- whoever tried to use it, so flagging it broken cannot be a moderator bit. Editing
                -- the assets themselves - warranty dates, the plumber's number - is
                -- ManageMaintenance and stays with Flatmates.
                UPDATE roles SET permissions = permissions + 144115188075855872
                WHERE (floor(permissions / 144115188075855872) % 2) = 0 AND type = 'everyone';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Scoped to @everyone: both bits are legitimately held by ordinary roles, so stripping
            // them wherever they appear would revoke grants this migration never made.
            migrationBuilder.Sql("""
                UPDATE roles SET permissions = permissions - 36028797018963968
                WHERE (floor(permissions / 36028797018963968) % 2) = 1 AND type = 'everyone';

                UPDATE roles SET permissions = permissions - 144115188075855872
                WHERE (floor(permissions / 144115188075855872) % 2) = 1 AND type = 'everyone';
                """);
        }
    }
}
