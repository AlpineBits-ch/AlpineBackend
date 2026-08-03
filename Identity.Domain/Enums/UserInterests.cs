namespace Identity.Domain.Enums;

/// <summary>
/// Which halves of the product an account said it came for, answered once at onboarding.
/// </summary>
[Flags]
public enum UserInterests
{
    None = 0,
    Isle = 1,
    Social = 2,
}

/// <summary>Translation between <see cref="UserInterests"/> and its wire form.</summary>
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

    /// <summary>Parses the wire form, refusing anything it does not recognise.</summary>
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
