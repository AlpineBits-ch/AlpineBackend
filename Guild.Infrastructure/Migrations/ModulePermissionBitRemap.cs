namespace Guild.Persistence.Migrations;

/// <summary>
/// Generates the SQL that moves the wiki and household permission bits out of the
/// <c>permissions</c> columns and into the new <c>module_permissions</c> columns, at their new
/// positions.
/// </summary>
public static class ModulePermissionBitRemap
{
    /// <summary>Old <c>Permissions</c> bit index to new <c>ModulePermissions</c> bit index. This is
    /// the same table documented on the <c>ModulePermissions</c> enum, and the two must agree.
    /// </summary>
    public static readonly (int OldBit, int NewBit, string Member)[] Mapping =
    [
        (23, 0, "ViewWiki"),
        (24, 1, "CreateWikiPages"),
        (25, 2, "EditOwnWikiPages"),
        (26, 3, "EditAnyWikiPage"),
        (27, 4, "DeleteWikiPages"),
        (28, 5, "ManageWikiRevisions"),
        (29, 6, "ManageWikiStructure"),
        (30, 7, "ModerateWikiComments"),
        (31, 8, "PublishWikiPublicly"),
        (39, 9, "ManageLists"),
        (40, 10, "AddListItems"),
        (41, 11, "CheckOffListItems"),
        (42, 12, "ManageChores"),
        (43, 13, "CompleteChores"),
        (44, 14, "ManageLedger"),
        (45, 15, "AddExpenses"),
        (46, 16, "ManagePantry"),
        (47, 17, "CreateDecisions"),
        (48, 18, "VoteDecisions"),
        (49, 19, "ManageGuests"),
        (55, 20, "PlanMeals"),
        (56, 21, "ManageMeals"),
        (57, 22, "LogMaintenance"),
        (58, 23, "ManageMaintenance"),
    ];

    /// <summary>The five column pairs carrying a permission mask: the role's own, and the allow/deny
    /// halves of the member-level and channel-level overwrites.</summary>
    public static readonly (string Table, string CoreColumn, string ModuleColumn)[] Columns =
    [
        ("roles", "permissions", "module_permissions"),
        ("guild_members", "allow_permissions", "allow_module_permissions"),
        ("guild_members", "deny_permissions", "deny_module_permissions"),
        ("channel_permissions", "allow_permissions", "allow_module_permissions"),
        ("channel_permissions", "deny_permissions", "deny_module_permissions"),
    ];

    /// <summary>Moves each mapped bit from the core column to the module column.</summary>
    public static string UpSql() => Build(up: true);

    /// <summary>
    /// The exact inverse: moves each bit back from the module column to the core column.
    /// </summary>
    public static string DownSql() => Build(up: false);

    private static string Build(bool up)
    {
        var sql = new System.Text.StringBuilder();

        foreach (var (table, coreColumn, moduleColumn) in Columns)
        {
            sql.AppendLine($"-- {table}.{coreColumn} -> {table}.{moduleColumn}");

            foreach (var (oldBit, newBit, member) in Mapping)
            {
                var oldValue = 1UL << oldBit;
                var newValue = 1UL << newBit;

                if (up)
                {
                    sql.AppendLine($"-- {member}: bit {oldBit} ({oldValue}) -> bit {newBit} ({newValue})");
                    sql.AppendLine($"UPDATE {table}");
                    sql.AppendLine($"SET {moduleColumn} = {moduleColumn} + {newValue},");
                    sql.AppendLine($"    {coreColumn} = {coreColumn} - {oldValue}");
                    sql.AppendLine($"WHERE (floor({coreColumn} / {oldValue}) % 2) = 1;");
                }
                else
                {
                    sql.AppendLine($"-- {member}: bit {newBit} ({newValue}) -> bit {oldBit} ({oldValue})");
                    sql.AppendLine($"UPDATE {table}");
                    sql.AppendLine($"SET {coreColumn} = {coreColumn} + {oldValue},");
                    sql.AppendLine($"    {moduleColumn} = {moduleColumn} - {newValue}");
                    sql.AppendLine($"WHERE (floor({moduleColumn} / {newValue}) % 2) = 1;");
                }

                sql.AppendLine();
            }
        }

        return sql.ToString();
    }
}
