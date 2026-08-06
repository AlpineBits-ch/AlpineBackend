using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <summary>
    /// Pure data migration - EF scaffolded it empty, and it stays that way apart from the SQL
    /// below.
    /// </summary>
    public partial class BackfillHouseholdEveryonePermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Seven bits joined Role.DefaultEveryonePermissions, via the new
            // Role.HouseholdEveryonePermissions.
            migrationBuilder.Sql("""
                -- AddListItems (2^40)
                UPDATE roles SET permissions = permissions + 1099511627776
                WHERE (floor(permissions / 1099511627776) % 2) = 0 AND type = 'everyone';

                -- CheckOffListItems (2^41). Ticking things off is the collaborative half of a
                -- shopping list and the single most common action in the module.
                UPDATE roles SET permissions = permissions + 2199023255552
                WHERE (floor(permissions / 2199023255552) % 2) = 0 AND type = 'everyone';

                -- CompleteChores (2^43). Not ManageChores: creating and re-weighting chores stays
                -- with the Flatmates role.
                UPDATE roles SET permissions = permissions + 8796093022208
                WHERE (floor(permissions / 8796093022208) % 2) = 0 AND type = 'everyone';

                -- AddExpenses (2^45). Adding an expense you paid, and editing your own. Not
                -- ManageLedger, which rewrites other people's money.
                UPDATE roles SET permissions = permissions + 35184372088832
                WHERE (floor(permissions / 35184372088832) % 2) = 0 AND type = 'everyone';

                -- ManagePantry (2^46). Reads as a moderator bit and is not one: the pantry has no
                -- separate "add stock" permission, so without this nobody can put milk in the
                -- fridge.
                UPDATE roles SET permissions = permissions + 70368744177664
                WHERE (floor(permissions / 70368744177664) % 2) = 0 AND type = 'everyone';

                -- CreateDecisions (2^47). Opening a question for the house to answer is the most
                -- ordinary thing a member does in a Decisions channel.
                UPDATE roles SET permissions = permissions + 140737488355328
                WHERE (floor(permissions / 140737488355328) % 2) = 0 AND type = 'everyone';

                -- VoteDecisions (2^48). A decision only carries on quorum of non-abstaining votes,
                -- so without this every decision expires unresolved.
                UPDATE roles SET permissions = permissions + 281474976710656
                WHERE (floor(permissions / 281474976710656) % 2) = 0 AND type = 'everyone';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Scoped to @everyone: all seven bits are legitimately held by ordinary roles, so
            // stripping them wherever they appear would revoke grants this migration never made.
            migrationBuilder.Sql("""
                UPDATE roles SET permissions = permissions - 1099511627776
                WHERE (floor(permissions / 1099511627776) % 2) = 1 AND type = 'everyone';

                UPDATE roles SET permissions = permissions - 2199023255552
                WHERE (floor(permissions / 2199023255552) % 2) = 1 AND type = 'everyone';

                UPDATE roles SET permissions = permissions - 8796093022208
                WHERE (floor(permissions / 8796093022208) % 2) = 1 AND type = 'everyone';

                UPDATE roles SET permissions = permissions - 35184372088832
                WHERE (floor(permissions / 35184372088832) % 2) = 1 AND type = 'everyone';

                UPDATE roles SET permissions = permissions - 70368744177664
                WHERE (floor(permissions / 70368744177664) % 2) = 1 AND type = 'everyone';

                UPDATE roles SET permissions = permissions - 140737488355328
                WHERE (floor(permissions / 140737488355328) % 2) = 1 AND type = 'everyone';

                UPDATE roles SET permissions = permissions - 281474976710656
                WHERE (floor(permissions / 281474976710656) % 2) = 1 AND type = 'everyone';
                """);
        }
    }
}
