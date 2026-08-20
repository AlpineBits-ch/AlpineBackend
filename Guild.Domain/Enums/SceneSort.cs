namespace Guild.Domain.Enums;

/// <summary>
/// How the scene list orders. A query parameter, never a column, so unlike
/// <see cref="SceneStatus"/> it needs no Postgres enum and no migration.
/// </summary>
public enum SceneSort
{
    /// <summary>The live board: scenes on a clock first, soonest due at the top, then by recency.</summary>
    Board = 0,

    Name = 1,

    /// <summary>What the archive asks for: most recently finished first.</summary>
    Ended = 2,
}
