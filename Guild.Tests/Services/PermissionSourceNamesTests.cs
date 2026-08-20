using Guild.Application.Services;

namespace Guild.Tests.Services;

/// <summary>
/// EffectivePermissionsEndpoint serializes PermissionSource via ToString(), so reordering a member
/// is harmless but renaming or removing one silently breaks every client. The client's
/// PermissionSourceKey union mirrors this exact name set.
/// </summary>
[TestFixture]
public class PermissionSourceNamesTests
{
    private static readonly string[] Expected =
    [
        nameof(PermissionSource.Base),
        nameof(PermissionSource.MemberGuildAllow),
        nameof(PermissionSource.MemberGuildDeny),
        nameof(PermissionSource.CategoryEveryoneAllow),
        nameof(PermissionSource.CategoryEveryoneDeny),
        nameof(PermissionSource.CategoryRoleAllow),
        nameof(PermissionSource.CategoryRoleDeny),
        nameof(PermissionSource.CategoryMemberAllow),
        nameof(PermissionSource.CategoryMemberDeny),
        nameof(PermissionSource.ChannelEveryoneAllow),
        nameof(PermissionSource.ChannelEveryoneDeny),
        nameof(PermissionSource.ChannelRoleAllow),
        nameof(PermissionSource.ChannelRoleDeny),
        nameof(PermissionSource.ChannelMemberAllow),
        nameof(PermissionSource.ChannelMemberDeny),
        nameof(PermissionSource.Implied),
        nameof(PermissionSource.Superadmin),
        nameof(PermissionSource.Muted),
        nameof(PermissionSource.ModuleDisabled),
        nameof(PermissionSource.SceneRestricted),
    ];

    [Test]
    public void NameSet_MatchesWhatTheClientMirrors()
    {
        var actual = Enum.GetNames<PermissionSource>();
        Assert.That(actual, Is.EquivalentTo(Expected));
    }
}
