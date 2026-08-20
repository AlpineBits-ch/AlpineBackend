namespace Guild.Persistence.Migrations;

/// <summary>
/// The data repair that has to run before the read-state uniqueness index can be created: two acks
/// for a channel with no read state yet each inserted their own row, and an index added on top of
/// those rows would simply fail to build.
/// </summary>
public static class ReadStateUniquenessRepair
{
    /// <summary>
    /// Collapses stacked <c>read_states</c> rows down to one per (member, channel), keeping the
    /// furthest-along cursor. A row that never got a timestamp is the one to lose.
    /// </summary>
    public const string DeduplicateReadStatesSql = """
        DELETE FROM read_states rs USING (
          SELECT id, row_number() OVER (
            PARTITION BY member_id, channel_id
            ORDER BY last_read_at DESC NULLS LAST, updated_at DESC, id DESC) AS rn
          FROM read_states) d
        WHERE rs.id = d.id AND d.rn > 1;
        """;
}
