using System.Numerics;
using Guild.Domain.Enums;

namespace Guild.Application.Services;

/// <summary>Which of the two overwrite layers is being applied.</summary>
public enum PermissionLayer
{
    Category,
    Channel,
}

/// <summary>The three tiers Discord's resolution order names, within one layer.</summary>
public enum PermissionTier
{
    Everyone,
    Role,
    Member,
}

/// <summary>The layer that last wrote a bit. Serialized by name, so a rename or a removal breaks
/// clients where a reorder does not.</summary>
public enum PermissionSource
{
    Base,
    MemberGuildAllow,
    MemberGuildDeny,
    CategoryEveryoneAllow,
    CategoryEveryoneDeny,
    CategoryRoleAllow,
    CategoryRoleDeny,
    CategoryMemberAllow,
    CategoryMemberDeny,
    ChannelEveryoneAllow,
    ChannelEveryoneDeny,
    ChannelRoleAllow,
    ChannelRoleDeny,
    ChannelMemberAllow,
    ChannelMemberDeny,
    Implied,
    Superadmin,
    Muted,

    /// <summary>The permission belongs to a module the guild has switched off or its plan does not
    /// cover, which no role or ownership can escalate past.</summary>
    ModuleDisabled,

    /// <summary>The channel is a cast-only scene the member has nobody in.</summary>
    SceneRestricted,
}

/// <summary>Records which layer last wrote each bit, as <see cref="GuildPermissionService"/>
/// resolves. Passed as null on the hot path, where it costs nothing.</summary>
public sealed class PermissionTrace
{
    private readonly Dictionary<Permissions, PermissionSource> _entries = new();

    public IReadOnlyDictionary<Permissions, PermissionSource> Entries => _entries;

    /// <summary>A bit nothing wrote came from the role union.</summary>
    public PermissionSource SourceOf(Permissions singleBit) =>
        _entries.TryGetValue(singleBit, out var source) ? source : PermissionSource.Base;

    public void Record(Permissions bits, PermissionSource source)
    {
        var remaining = (ulong)bits;
        while (remaining != 0)
        {
            var bit = (Permissions)(1UL << BitOperations.TrailingZeroCount(remaining));
            _entries[bit] = source;
            remaining &= remaining - 1;
        }
    }

    /// <summary>Bits the deny actually removed. Those the overwrite named carry its source; the
    /// rest were taken by the reverse closure and belong to nobody.</summary>
    public void RecordDeny(Permissions changed, Permissions named, PermissionLayer layer, PermissionTier tier)
    {
        Record(changed & named, DenySource(layer, tier));
        Record(changed & ~named, PermissionSource.Implied);
    }

    public void RecordAllow(Permissions changed, PermissionLayer layer, PermissionTier tier) =>
        Record(changed, AllowSource(layer, tier));

    private static PermissionSource DenySource(PermissionLayer layer, PermissionTier tier) =>
        (layer, tier) switch
        {
            (PermissionLayer.Category, PermissionTier.Everyone) => PermissionSource.CategoryEveryoneDeny,
            (PermissionLayer.Category, PermissionTier.Role) => PermissionSource.CategoryRoleDeny,
            (PermissionLayer.Category, PermissionTier.Member) => PermissionSource.CategoryMemberDeny,
            (PermissionLayer.Channel, PermissionTier.Everyone) => PermissionSource.ChannelEveryoneDeny,
            (PermissionLayer.Channel, PermissionTier.Role) => PermissionSource.ChannelRoleDeny,
            _ => PermissionSource.ChannelMemberDeny,
        };

    private static PermissionSource AllowSource(PermissionLayer layer, PermissionTier tier) =>
        (layer, tier) switch
        {
            (PermissionLayer.Category, PermissionTier.Everyone) => PermissionSource.CategoryEveryoneAllow,
            (PermissionLayer.Category, PermissionTier.Role) => PermissionSource.CategoryRoleAllow,
            (PermissionLayer.Category, PermissionTier.Member) => PermissionSource.CategoryMemberAllow,
            (PermissionLayer.Channel, PermissionTier.Everyone) => PermissionSource.ChannelEveryoneAllow,
            (PermissionLayer.Channel, PermissionTier.Role) => PermissionSource.ChannelRoleAllow,
            _ => PermissionSource.ChannelMemberAllow,
        };
}

/// <summary>Who a trace is being resolved for.</summary>
public enum PermissionSubjectKind
{
    Role,
    Member,
}

/// <summary>A role id or a guild member id, with which one it is.</summary>
public readonly record struct PermissionSubject(PermissionSubjectKind Kind, string Id);

/// <summary>One subject's resolved permissions in one channel, with the layer that decided each
/// bit.</summary>
public sealed class ResolvedChannelPermissions
{
    public required Permissions Permissions { get; init; }
    public required ModulePermissions ModulePermissions { get; init; }
    public required IReadOnlyDictionary<Permissions, PermissionSource> Sources { get; init; }
}
