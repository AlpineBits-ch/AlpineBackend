using Guild.Application.Services;
using Guild.Domain.Enums;

namespace Guild.Tests.Services;

/// <summary>
/// The client mirrors this table to warn before a deny takes more than it names. Both sides pin the
/// same list, so a change here breaks a test rather than drifting silently.
/// See docs/specs/channel-permissions-ux.md, "Golden list".
/// </summary>
[TestFixture]
public class ImpliedPermissionsTableTests
{
    private static readonly (Permissions Holder, Permissions Implied)[] Golden =
    [
        (Permissions.EditAnyMessage, Permissions.EditOwnMessages),
        (Permissions.DeleteAnyMessage, Permissions.DeleteOwnMessages),
        (Permissions.ManageAnyThread, Permissions.ManageOwnThreads),
        (Permissions.Speak, Permissions.Connect),
        (Permissions.Stream, Permissions.Connect),
        (Permissions.MuteMembers, Permissions.Connect),
        (Permissions.DeafenMembers, Permissions.Connect),
        (Permissions.MoveMembers, Permissions.Connect),
        (Permissions.PinMessages, Permissions.SendMessages),
        (Permissions.AttachFiles, Permissions.SendMessages),
        (Permissions.EmbedLinks, Permissions.SendMessages),
        (Permissions.AddReactions, Permissions.SendMessages),
        (Permissions.CreateThreads, Permissions.SendMessages),
        (Permissions.SendMessages, Permissions.ViewChannel),
        (Permissions.SendMessagesInThreads, Permissions.ViewChannel),
        (Permissions.Connect, Permissions.ViewChannel),
        (Permissions.EditOwnMessages, Permissions.ViewChannel),
        (Permissions.DeleteOwnMessages, Permissions.ViewChannel),
        (Permissions.ManageOwnThreads, Permissions.ViewChannel),
        (Permissions.ManagePermissions, Permissions.ViewChannel),
        (Permissions.ManageChannel, Permissions.ViewChannel),
    ];

    [Test]
    public void EveryGoldenPair_IsImpliedByTheForwardClosure()
    {
        foreach (var (holder, implied) in Golden)
        {
            var expanded = GuildPermissionService.ExpandImpliedPermissions(holder);
            Assert.That(expanded.HasFlag(implied), Is.True, $"{holder} should imply {implied}");
        }
    }

    [Test]
    public void EveryGoldenPair_IsTakenByTheReverseClosure()
    {
        foreach (var (holder, implied) in Golden)
        {
            var expanded = GuildPermissionService.ExpandDeniedPermissions(implied);
            Assert.That(expanded.HasFlag(holder), Is.True, $"denying {implied} should also deny {holder}");
        }
    }

    /// <summary>The documented collateral of the deny that matters most.</summary>
    [Test]
    public void DenyingViewChannel_TakesExactlyTheDocumentedSet()
    {
        var expanded = GuildPermissionService.ExpandDeniedPermissions(Permissions.ViewChannel);

        Permissions expected =
            Permissions.ViewChannel | Permissions.SendMessages | Permissions.SendMessagesInThreads |
            Permissions.Connect | Permissions.EditOwnMessages | Permissions.DeleteOwnMessages |
            Permissions.ManageOwnThreads | Permissions.ManagePermissions | Permissions.ManageChannel |
            Permissions.PinMessages | Permissions.AttachFiles | Permissions.EmbedLinks |
            Permissions.AddReactions | Permissions.CreateThreads | Permissions.Speak |
            Permissions.Stream | Permissions.MuteMembers | Permissions.DeafenMembers |
            Permissions.MoveMembers | Permissions.EditAnyMessage | Permissions.DeleteAnyMessage |
            Permissions.ManageAnyThread;

        Assert.That(expanded, Is.EqualTo(expected));
    }
}
