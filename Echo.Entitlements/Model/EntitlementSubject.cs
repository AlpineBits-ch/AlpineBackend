namespace Echo.Entitlements.Model;

/// <summary>What an entitlement can be attached to.</summary>
public enum SubjectKind
{
    User,
    Guild,
}

/// <summary>Whose entitlements are being asked about.</summary>
public readonly record struct EntitlementSubject(SubjectKind Kind, string Id)
{
    public static EntitlementSubject ForGuild(string guildId) => new(SubjectKind.Guild, Require(guildId));

    public static EntitlementSubject ForUser(string userId) => new(SubjectKind.User, Require(userId));

    public override string ToString() => $"{Kind}:{Id}";

    private static string Require(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return id;
    }
}
