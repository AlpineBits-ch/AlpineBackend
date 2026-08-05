using Guild.Domain.Aggregates;
using Guild.Domain.Enums;

namespace Guild.Tests.Domain;

/// <summary>
/// Pins the @everyone default grant and the one helper that external permission masks must go
/// through.
/// </summary>
[TestFixture]
public class EveryoneRoleDefaultsTests
{
    private const string GuildId = "guild-1";
    private const string MemberId = "member-1";

    // ── The default grant ─────────────────────────────────────────────────────

    [Test]
    public void DefaultEveryonePermissions_GrantsEverythingDiscordGrantsItsOwnEveryone()
    {
        Permissions[] expected =
        [
            Permissions.ViewChannel,
            Permissions.SendMessages,
            Permissions.EditOwnMessages,
            Permissions.DeleteOwnMessages,
            Permissions.AddReactions,
            Permissions.AttachFiles,
            Permissions.EmbedLinks,
            Permissions.CreateThreads,
            Permissions.SendMessagesInThreads,
            Permissions.ManageOwnThreads,
            Permissions.Connect,
            Permissions.Speak,
            Permissions.Stream,
            Permissions.CreateInvite,
            Permissions.ChangeNickname,
            Permissions.ViewWiki,
        ];

        Assert.Multiple(() =>
        {
            foreach (var permission in expected)
            {
                Assert.That(Role.DefaultEveryonePermissions.HasFlag(permission), Is.True,
                    $"{permission} must be part of the @everyone default");
            }
        });
    }

    [Test]
    public void DefaultEveryonePermissions_WithheldBitsStayWithheld()
    {
        // PinMessages sits behind Manage Messages in Discord.
        Permissions[] withheld =
        [
            Permissions.PinMessages,
            Permissions.MentionEveryone,
            Permissions.EditAnyMessage,
            Permissions.DeleteAnyMessage,
            Permissions.ManageAnyThread,
            Permissions.ManageChannel,
            Permissions.ManagePermissions,
            Permissions.ManageRoles,
            Permissions.ManageWebhooks,
            Permissions.ManageNicknames,
            Permissions.ManageGuild,
            Permissions.KickMembers,
            Permissions.BanMembers,
            Permissions.ModerateMembers,
            Permissions.ViewAuditLog,
            Permissions.MuteMembers,
            Permissions.DeafenMembers,
            Permissions.MoveMembers,
            Permissions.ManageEmojis,
            Permissions.ManageEvents,
            Permissions.Superadmin,
        ];

        Assert.Multiple(() =>
        {
            foreach (var permission in withheld)
            {
                Assert.That(Role.DefaultEveryonePermissions.HasFlag(permission), Is.False,
                    $"{permission} must NOT be part of the @everyone default");
            }
        });
    }

    [Test]
    public void DefaultEveryonePermissions_GrantsNoWikiBitBeyondReading()
    {
        Permissions[] authoringAndModeration =
        [
            Permissions.CreateWikiPages,
            Permissions.EditOwnWikiPages,
            Permissions.EditAnyWikiPage,
            Permissions.DeleteWikiPages,
            Permissions.ManageWikiRevisions,
            Permissions.ManageWikiStructure,
            Permissions.ModerateWikiComments,
            Permissions.PublishWikiPublicly,
        ];

        Assert.Multiple(() =>
        {
            Assert.That(Role.DefaultEveryonePermissions.HasFlag(Permissions.ViewWiki), Is.True);
            foreach (var permission in authoringAndModeration)
            {
                Assert.That(Role.DefaultEveryonePermissions.HasFlag(permission), Is.False,
                    $"{permission} is an authoring/moderation bit and must stay with staff");
            }
        });
    }

    [Test]
    public void DefaultEveryonePermissions_GrantsNoHouseholdModuleBit()
    {
        // Household participation is a separate product decision, explicitly out of scope for the
        // Discord-parity line this default draws.
        Permissions[] household =
        [
            Permissions.ManageLists, Permissions.AddListItems, Permissions.CheckOffListItems,
            Permissions.ManageChores, Permissions.CompleteChores,
            Permissions.ManageLedger, Permissions.AddExpenses,
            Permissions.ManagePantry,
            Permissions.CreateDecisions, Permissions.VoteDecisions,
            Permissions.ManageGuests,
        ];

        Assert.Multiple(() =>
        {
            foreach (var permission in household)
            {
                Assert.That(Role.DefaultEveryonePermissions.HasFlag(permission), Is.False,
                    $"{permission} is a household-module bit and must not be an @everyone default");
            }
        });
    }

    [Test]
    public void CreateEveryoneRole_UsesTheSharedDefault()
    {
        var role = Role.CreateEveryoneRole(GuildId, MemberId);

        Assert.Multiple(() =>
        {
            Assert.That(role.Permissions, Is.EqualTo(Role.DefaultEveryonePermissions),
                "the constant is the single source of truth; the factory must not re-list bits");
            Assert.That(role.Type, Is.EqualTo(RoleType.Everyone));
            Assert.That(role.Position, Is.Zero);
            Assert.That(role.Members, Has.Count.EqualTo(1), "the founding member joins @everyone");
        });
    }

    // ── The external-mask baseline ────────────────────────────────────────────

    [Test]
    public void ExternalEveryoneBaseline_IsASubsetOfTheDefault()
    {
        // If a bit is worth restoring on an imported role it is worth granting on a native one.
        Assert.That(Role.DefaultEveryonePermissions & Role.ExternalEveryoneBaseline,
            Is.EqualTo(Role.ExternalEveryoneBaseline));
    }

    [Test]
    public void ApplyExternalEveryonePermissions_KeepsTheExternalMaskAndRestoresTheBaseline()
    {
        var role = Role.CreateEveryoneRole(GuildId, MemberId);

        // What DiscordPermissionMapper can actually produce for a typical @everyone role.
        var imported = Permissions.ViewChannel | Permissions.SendMessages | Permissions.AddReactions |
                       Permissions.Connect | Permissions.Speak;

        role.ApplyExternalEveryonePermissions(imported);

        Assert.Multiple(() =>
        {
            Assert.That(role.Permissions.HasFlag(Permissions.ViewChannel), Is.True);
            Assert.That(role.Permissions.HasFlag(Permissions.SendMessages), Is.True);
            Assert.That(role.Permissions.HasFlag(Permissions.Speak), Is.True);

            Assert.That(role.Permissions.HasFlag(Permissions.ViewWiki), Is.True,
                "Discord cannot express ViewWiki, so it must be restored rather than lost");
            Assert.That(role.Permissions.HasFlag(Permissions.ManageOwnThreads), Is.True);
            Assert.That(role.Permissions.HasFlag(Permissions.EditOwnMessages), Is.True);
            Assert.That(role.Permissions.HasFlag(Permissions.DeleteOwnMessages), Is.True);

            // Bits the external mask did not carry stay off - this is a floor, not a reset.
            Assert.That(role.Permissions.HasFlag(Permissions.CreateInvite), Is.False,
                "an external mask that withheld CreateInvite must keep it withheld");
            Assert.That(role.Permissions.HasFlag(Permissions.Stream), Is.False);
        });
    }

    [Test]
    public void ApplyExternalEveryonePermissions_EmptyMask_YieldsExactlyTheBaseline()
    {
        var role = Role.CreateEveryoneRole(GuildId, MemberId);

        role.ApplyExternalEveryonePermissions(Permissions.None);

        Assert.That(role.Permissions, Is.EqualTo(Role.ExternalEveryoneBaseline));
    }

    [Test]
    public void ApplyExternalEveryonePermissions_PreservesElevatedBitsTheExternalMaskCarried()
    {
        // A Discord @everyone role really can hold Administrator, which the mapper turns into
        // Superadmin. The floor must not quietly drop it.
        var role = Role.CreateEveryoneRole(GuildId, MemberId);

        role.ApplyExternalEveryonePermissions(Permissions.Superadmin);

        Assert.That(role.Permissions.HasFlag(Permissions.Superadmin), Is.True);
    }

    [Test]
    public void ApplyExternalEveryonePermissions_OnAnOrdinaryRole_Throws()
    {
        var role = Role.Create(new CreateRoleParams
        {
            Name = "vip", GuildId = GuildId, Permissions = Permissions.ViewChannel,
        });

        Assert.Throws<InvalidOperationException>(
            () => role.ApplyExternalEveryonePermissions(Permissions.SendMessages),
            "the baseline is meaningful only for @everyone; ordinary roles must set Permissions directly");
    }
}
