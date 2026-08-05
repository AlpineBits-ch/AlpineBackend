using Persistence;
using Social.Domain.Enums;

namespace Social.Domain.Aggregate;

/// <summary>One executable-matching rule for a <see cref="GameApplication"/>.</summary>
public class GameExecutable : BaseEntity<GameExecutable>, IPrefixedEntity
{
    public static string Prefix { get; } = "gexe";

    public string GameApplicationId { get; set; } = null!;
    public virtual GameApplication GameApplication { get; set; } = null!;

    /// <summary>
    /// The rule, normalized at seed time: lower-cased, backslashes folded to forward slashes, and
    /// any leading negation marker stripped into <see cref="IsNegated"/>. May contain slashes.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>The final path segment of <see cref="Name"/>.</summary>
    public string Basename { get; set; } = null!;

    public GamePlatform Os { get; set; }

    /// <summary>
    /// True when this executable is the store/launcher front-end rather than the game.
    /// </summary>
    public bool IsLauncher { get; set; }

    /// <summary>
    /// An exclusion: if this executable is running, the parent application is NOT a match.
    /// </summary>
    public bool IsNegated { get; set; }
}
