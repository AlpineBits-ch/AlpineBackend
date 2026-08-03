namespace Identity.Domain.Enums;

/// <summary>
/// Which halves of the product an account said it came for, answered once at onboarding.
///
/// <para>Venta ships two products in one binary - proximity voice for The Isle, and an encrypted
/// chat client - and only the second needs a master key. This is what lets an account that only
/// wants the first skip the recovery-code ceremony at first launch, and what the client consults
/// to decide whether to raise it later.</para>
///
/// <para>Flags rather than a table: there are two members, the set is answered atomically, and
/// nothing queries by individual interest. One int column is the whole storage cost.</para>
/// </summary>
[Flags]
public enum UserInterests
{
    None = 0,
    Isle = 1,
    Social = 2,
}

/// <summary>
/// Translation between <see cref="UserInterests"/> and its wire form.
///
/// <para>The wire form is a lowercase string array (<c>["isle","social"]</c>), not the serialized
/// enum. A <c>[Flags]</c> enum under the globally registered <c>JsonStringEnumConverter</c> would
/// go out as the single string <c>"Isle, Social"</c>, which is a comma-separated list wearing a
/// string's clothing - awkward to parse, and it changes shape the moment a third member appears.
/// The array says what it means and every client already reads it that way.</para>
/// </summary>
public static class UserInterestsExtensions
{
    private const string IsleName = "isle";
    private const string SocialName = "social";

    public static string[] ToWire(this UserInterests interests)
    {
        var names = new List<string>(2);
        if (interests.HasFlag(UserInterests.Isle)) names.Add(IsleName);
        if (interests.HasFlag(UserInterests.Social)) names.Add(SocialName);
        return names.ToArray();
    }

    /// <summary>
    /// Parses the wire form, refusing anything it does not recognise.
    ///
    /// <para>Refuses rather than ignores, deliberately. Silently dropping an unknown name would
    /// let a client that believes it selected something end up stored as having selected nothing,
    /// and the disagreement would only surface as a launch sequence doing the wrong thing much
    /// later. An empty result is refused for the same reason: no interest at all is not a state
    /// the client can render.</para>
    /// </summary>
    public static bool TryParseWire(IEnumerable<string>? names, out UserInterests result)
    {
        result = UserInterests.None;
        if (names is null) return false;

        foreach (var name in names)
        {
            switch (name?.Trim().ToLowerInvariant())
            {
                case IsleName:
                    result |= UserInterests.Isle;
                    break;
                case SocialName:
                    result |= UserInterests.Social;
                    break;
                default:
                    result = UserInterests.None;
                    return false;
            }
        }

        return result != UserInterests.None;
    }
}
