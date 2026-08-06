using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Guild.Persistence.Migrations
{
    /// <summary>
    /// Repairs a schema drift: the model snapshot has described
    /// <c>chore_occurrences.reminded_at</c>, its filtered index, and six <c>audit_action_type</c>
    /// members since 20260806093730, but no migration ever created them.
    /// </summary>
    public partial class RepairChoreRemindedAtAndHouseholdAuditActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE chore_occurrences
                    ADD COLUMN IF NOT EXISTS reminded_at timestamp with time zone;
                """);

            // Matches the snapshot's filter exactly.
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS ix_chore_occurrences_due_at
                    ON chore_occurrences (due_at)
                    WHERE reminded_at IS NULL AND completed_at IS NULL AND skipped_at IS NULL;
                """);

            // Six members of audit_action_type, in the same shape as 20260805054125 in Social and
            // for the same two reasons: EF wraps a migration in a transaction, PostgreSQL has
            // historically refused ALTER TYPE ...
            migrationBuilder.Sql(
                "ALTER TYPE audit_action_type ADD VALUE IF NOT EXISTS 'expense_created' BEFORE 'forum_config_updated';",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "ALTER TYPE audit_action_type ADD VALUE IF NOT EXISTS 'expense_deleted' BEFORE 'forum_config_updated';",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "ALTER TYPE audit_action_type ADD VALUE IF NOT EXISTS 'expense_updated' BEFORE 'forum_config_updated';",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "ALTER TYPE audit_action_type ADD VALUE IF NOT EXISTS 'ledger_config_updated' BEFORE 'member_banned';",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "ALTER TYPE audit_action_type ADD VALUE IF NOT EXISTS 'member_moved_out' BEFORE 'member_muted';",
                suppressTransaction: true);
            migrationBuilder.Sql(
                "ALTER TYPE audit_action_type ADD VALUE IF NOT EXISTS 'settlement_recorded' BEFORE 'template_created';",
                suppressTransaction: true);
        }

        /// <summary>Inverts only the two schema objects this migration can invert.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_chore_occurrences_due_at;");
            migrationBuilder.Sql("ALTER TABLE chore_occurrences DROP COLUMN IF EXISTS reminded_at;");
        }
    }
}
