using Echo.Entitlements.Keys;

namespace Echo.Entitlements.Model;

/// <summary>One entitlement value, in whichever of the three shapes its key declares.</summary>
public readonly record struct EntitlementValue
{
    /// <summary>The numeric limit that means "no limit".</summary>
    public const long Unlimited = long.MaxValue;

    private EntitlementValue(EntitlementValueKind kind, long raw)
    {
        Kind = kind;
        Raw = raw;
    }

    public EntitlementValueKind Kind { get; }

    /// <summary>The ordered payload.</summary>
    public long Raw { get; }

    public static EntitlementValue OfFlag(bool granted) =>
        new(EntitlementValueKind.Flag, granted ? 1 : 0);

    public static EntitlementValue OfNumber(long limit) =>
        limit < 0
            ? throw new ArgumentOutOfRangeException(nameof(limit),
                "An entitlement limit cannot be negative. Use 0 for 'none' and EntitlementValue.Unlimited for 'no ceiling'.")
            : new EntitlementValue(EntitlementValueKind.Numeric, limit);

    /// <summary>A ladder value as a rank.</summary>
    public static EntitlementValue OfRank(int rank) =>
        rank < 0
            ? throw new ArgumentOutOfRangeException(nameof(rank), "A ladder rank cannot be negative.")
            : new EntitlementValue(EntitlementValueKind.Ladder, rank);

    public bool AsFlag => Require(EntitlementValueKind.Flag).Raw != 0;

    public long AsNumber => Require(EntitlementValueKind.Numeric).Raw;

    public int AsRank => (int)Require(EntitlementValueKind.Ladder).Raw;

    public bool IsUnlimited => Kind == EntitlementValueKind.Numeric && Raw == Unlimited;

    /// <summary>
    /// The merge rule across sources from spec section 4.1: flags OR, numeric limits take the
    /// maximum, ladders take the highest rank.
    /// </summary>
    public static EntitlementValue Merge(EntitlementValue left, EntitlementValue right)
    {
        RequireSameKind(left, right);

        return left.Kind switch
        {
            EntitlementValueKind.Flag => OfFlag(left.AsFlag || right.AsFlag),
            EntitlementValueKind.Numeric => OfNumber(Math.Max(left.AsNumber, right.AsNumber)),
            EntitlementValueKind.Ladder => OfRank(Math.Max(left.AsRank, right.AsRank)),
            _ => throw new ArgumentOutOfRangeException(nameof(left), left.Kind, "Unknown entitlement value kind."),
        };
    }

    /// <summary>
    /// The opposite of <see cref="Merge"/>, and the whole reason paired keys exist: flags AND,
    /// numeric limits take the minimum, ladders take the lowest rank.
    /// </summary>
    public static EntitlementValue Restrict(EntitlementValue left, EntitlementValue right)
    {
        RequireSameKind(left, right);

        return left.Kind switch
        {
            EntitlementValueKind.Flag => OfFlag(left.AsFlag && right.AsFlag),
            EntitlementValueKind.Numeric => OfNumber(Math.Min(left.AsNumber, right.AsNumber)),
            EntitlementValueKind.Ladder => OfRank(Math.Min(left.AsRank, right.AsRank)),
            _ => throw new ArgumentOutOfRangeException(nameof(left), left.Kind, "Unknown entitlement value kind."),
        };
    }

    /// <summary>True when <paramref name="candidate"/> is at least as generous as this value, under
    /// whichever order the kind implies. Enforcement sites use this to decide whether a request has
    /// to be degraded.</summary>
    public bool AtLeast(EntitlementValue candidate)
    {
        RequireSameKind(this, candidate);
        return candidate.Raw >= Raw;
    }

    public override string ToString() => Kind switch
    {
        EntitlementValueKind.Flag => Raw != 0 ? "true" : "false",
        EntitlementValueKind.Numeric => Raw == Unlimited ? "unlimited" : Raw.ToString(),
        _ => $"rank {Raw}",
    };

    private EntitlementValue Require(EntitlementValueKind kind) =>
        Kind == kind
            ? this
            : throw new InvalidOperationException(
                $"This entitlement value is a {Kind}, not a {kind}.");

    private static void RequireSameKind(EntitlementValue left, EntitlementValue right)
    {
        if (left.Kind != right.Kind)
        {
            throw new InvalidOperationException(
                $"Cannot combine a {left.Kind} entitlement value with a {right.Kind} one. "
                + "Two sources disagree about the shape of a key, which means one of them was built "
                + "against a different catalogue.");
        }
    }
}
