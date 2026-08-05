using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEveryoneWikiAndOwnContentPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── @everyone default widening ───────────────────────────────────────────────────
            // Four bits joined Role.DefaultEveryonePermissions.
            migrationBuilder.Sql("""
                -- EditOwnMessages (2^2). Discord does not gate editing your own message behind a
                -- permission at all, so the faithful default is "granted".
                UPDATE roles
                SET permissions = permissions + 4
                WHERE (floor(permissions / 4) % 2) = 0
                  AND type = 'everyone';

                -- DeleteOwnMessages (2^4). Same reasoning as EditOwnMessages.
                UPDATE roles
                SET permissions = permissions + 16
                WHERE (floor(permissions / 16) % 2) = 0
                  AND type = 'everyone';

                -- ManageOwnThreads (2^18). Pairs with CreateThreads, which @everyone has held all
                -- along; in Discord the thread creator can archive their own thread without holding
                -- Manage Threads.
                UPDATE roles
                SET permissions = permissions + 262144
                WHERE (floor(permissions / 262144) % 2) = 0
                  AND type = 'everyone';

                -- ViewWiki (2^23). No Discord counterpart; granted because a wiki no member can
                -- read is not a useful default. Only the read bit - authoring and moderation stay
                -- with whichever roles were granted them.
                UPDATE roles
                SET permissions = permissions + 8388608
                WHERE (floor(permissions / 8388608) % 2) = 0
                  AND type = 'everyone';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Scoped to @everyone, unlike 20260730182528's blanket strip: all four of these bits
            // are legitimately held by ordinary roles, so removing them wherever they appear would
            // revoke grants this migration never made.
            migrationBuilder.Sql("""
                UPDATE roles SET permissions = permissions - 4
                WHERE (floor(permissions / 4) % 2) = 1 AND type = 'everyone';

                UPDATE roles SET permissions = permissions - 16
                WHERE (floor(permissions / 16) % 2) = 1 AND type = 'everyone';

                UPDATE roles SET permissions = permissions - 262144
                WHERE (floor(permissions / 262144) % 2) = 1 AND type = 'everyone';

                UPDATE roles SET permissions = permissions - 8388608
                WHERE (floor(permissions / 8388608) % 2) = 1 AND type = 'everyone';
                """);
        }
    }
}
