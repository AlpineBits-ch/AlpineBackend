namespace Guild.Persistence.Migrations;

/// <summary>
/// The data repair that has to run before the two uniqueness indexes can be created: every row that
/// violates them today was written by code that has since been fixed, and an index added on top of
/// the existing rows would simply fail to build.
/// </summary>
public static class RoleUniquenessRepair
{
    /// <summary>
    /// Demotes every @everyone role in a guild except the first one to <c>RoleType.None</c>.
    /// </summary>
    public const string DemoteCounterfeitEveryoneRolesSql = """
        UPDATE roles SET type = 'none'
        WHERE id IN (
            SELECT id FROM (
                SELECT id, row_number() OVER (
                    PARTITION BY guild_id
                    ORDER BY created_at ASC, id ASC) AS rn
                FROM roles
                WHERE type = 'everyone') ranked
            WHERE ranked.rn > 1);
        """;

    /// <summary>
    /// Collapses stacked <c>role_members</c> rows down to one per (role, member).
    /// </summary>
    public const string DeduplicateRoleMembersSql = """
        DELETE FROM role_members rm USING (
          SELECT id, row_number() OVER (
            PARTITION BY role_id, member_id
            ORDER BY (expires_at IS NULL) DESC, expires_at DESC NULLS FIRST, created_at ASC, id ASC) AS rn
          FROM role_members) d
        WHERE rm.id = d.id AND d.rn > 1;
        """;
}
