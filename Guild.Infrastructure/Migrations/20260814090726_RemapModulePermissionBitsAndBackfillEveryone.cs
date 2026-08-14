using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <summary>The data half of the core/module permission split.</summary>
    public partial class RemapModulePermissionBitsAndBackfillEveryone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── 1.
            migrationBuilder.Sql(ModulePermissionBitRemap.UpSql());

            // ── 2.
            migrationBuilder.Sql("UPDATE roles SET mentionable = true;");

            // ── 3.
            migrationBuilder.Sql("""
                -- ReadMessageHistory (2^23)
                UPDATE roles SET permissions = permissions + 8388608
                WHERE (floor(permissions / 8388608) % 2) = 0 AND type = 'everyone';

                -- SendVoiceMessages (2^24)
                UPDATE roles SET permissions = permissions + 16777216
                WHERE (floor(permissions / 16777216) % 2) = 0 AND type = 'everyone';

                -- SendPolls (2^25)
                UPDATE roles SET permissions = permissions + 33554432
                WHERE (floor(permissions / 33554432) % 2) = 0 AND type = 'everyone';

                -- UseExternalEmojis (2^26)
                UPDATE roles SET permissions = permissions + 67108864
                WHERE (floor(permissions / 67108864) % 2) = 0 AND type = 'everyone';

                -- UseExternalStickers (2^27)
                UPDATE roles SET permissions = permissions + 134217728
                WHERE (floor(permissions / 134217728) % 2) = 0 AND type = 'everyone';

                -- UseApplicationCommands (2^29)
                UPDATE roles SET permissions = permissions + 536870912
                WHERE (floor(permissions / 536870912) % 2) = 0 AND type = 'everyone';

                -- UseVoiceActivity (2^41)
                UPDATE roles SET permissions = permissions + 2199023255552
                WHERE (floor(permissions / 2199023255552) % 2) = 0 AND type = 'everyone';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Mirror image, in reverse order: strip the parity bits from @everyone first, so that
            // bits 23-31 and 39-41 are free again before the remap moves the module bits back into
            // them.
            migrationBuilder.Sql("""
                UPDATE roles SET permissions = permissions - 8388608
                WHERE (floor(permissions / 8388608) % 2) = 1 AND type = 'everyone';

                UPDATE roles SET permissions = permissions - 16777216
                WHERE (floor(permissions / 16777216) % 2) = 1 AND type = 'everyone';

                UPDATE roles SET permissions = permissions - 33554432
                WHERE (floor(permissions / 33554432) % 2) = 1 AND type = 'everyone';

                UPDATE roles SET permissions = permissions - 67108864
                WHERE (floor(permissions / 67108864) % 2) = 1 AND type = 'everyone';

                UPDATE roles SET permissions = permissions - 134217728
                WHERE (floor(permissions / 134217728) % 2) = 1 AND type = 'everyone';

                UPDATE roles SET permissions = permissions - 536870912
                WHERE (floor(permissions / 536870912) % 2) = 1 AND type = 'everyone';

                UPDATE roles SET permissions = permissions - 2199023255552
                WHERE (floor(permissions / 2199023255552) % 2) = 1 AND type = 'everyone';
                """);

            // Nothing to undo for `mentionable`: the column itself is dropped by the schema
            // migration's Down, so restoring the all-false state it never usefully had would be
            // writing to a column on its way out.

            migrationBuilder.Sql(ModulePermissionBitRemap.DownSql());
        }
    }
}
