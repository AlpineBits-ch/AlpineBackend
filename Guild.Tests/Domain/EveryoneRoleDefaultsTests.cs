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
    public void DefaultEveryonePermissions_GrantsEveryHouseholdParticipationBit()
    {
        // Without these a Household guild is read-only for every member except the owner, whose
        // Superadmin short-circuit hides the problem from whoever created it.
        Permissions[] participation =
        [
            Permissions.AddListItems, Permissions.CheckOffListItems,
            Permissions.CompleteChores,
            Permissions.AddExpenses,
            Permissions.ManagePantry,
            Permissions.CreateDecisions, Permissions.VoteDecisions,
        ];

        Assert.Multiple(() =>
        {
            foreach (var permission in participation)
            {
                Assert.That(Role.DefaultEveryonePermissions.HasFlag(permission), Is.True,
                    $"{permission} is what an ordinary flatmate does and must be an @everyone default");
            }
        });
    }

    [Test]
    public void DefaultEveryonePermissions_GrantsNoHouseholdModeratorBit()
    {
        // The asymmetric bits - the ones that change something belonging to somebody else - stay
        // with the seeded Flatmates role and the owner.
        Permissions[] moderation =
        [
            Permissions.ManageLists,
            Permissions.ManageChores,
            Permissions.ManageLedger,
            Permissions.ManageGuests,
        ];

        Assert.Multiple(() =>
        {
            foreach (var permission in moderation)
            {
                Assert.That(Role.DefaultEveryonePermissions.HasFlag(permission), Is.False,
                    $"{permission} rewrites someone else's row and must not be an @everyone default");
            }
        });
    }

    [Test]
    public void HouseholdEveryoneAndFlatmatePermissions_DoNotOverlap()
    {
        // The Flatmates role exists to add what @everyone deliberately lacks.
        Assert.That(Role.HouseholdEveryonePermissions & Role.FlatmatePermissions,
            Is.EqualTo(Permissions.None));
    }

    [Test]
    public void EveryHouseholdPermissionBit_IsInExactlyOneOfTheTwoSets()
    {
        // Guards against a new household permission being added to the enum and to GuildFeatureMap
        // while nobody decides whether a flatmate or only a manager should hold it - the failure
        // mode that produced the read-only-household bug in the first place.
        Permissions[] allHousehold =
        [
            Permissions.ManageLists, Permissions.AddListItems, Permissions.CheckOffListItems,
            Permissions.ManageChores, Permissions.CompleteChores,
            Permissions.ManageLedger, Permissions.AddExpenses,
            Permissions.ManagePantry,
            Permissions.CreateDecisions, Permissions.VoteDecisions,
            Permissions.ManageGuests,
        ];

        var covered = Role.HouseholdEveryonePermissions | Role.FlatmatePermissions;

        Assert.Multiple(() =>
        {
            foreach (var permission in allHousehold)
            {
                Assert.That(covered.HasFlag(permission), Is.True,
                    $"{permission} belongs in HouseholdEveryonePermissions or FlatmatePermissions");
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

    /// <summary>Guards the coupling between the constant and the literals in
    /// 20260806093730_BackfillHouseholdEveryonePermissions. If a bit joins
    /// HouseholdEveryonePermissions without a matching UPDATE, new guilds get it and every existing
    /// one silently does not - which is the exact shape of the bug that migration exists to fix.</summary>
    [Test]
    public void HouseholdEveryonePermissionsMatchTheMigrationLiterals()
    {
        // The 2^n values the migration adds, in the order they appear in it.
        ulong[] literals =
        [
            1099511627776,      // AddListItems         2^40
            2199023255552,      // CheckOffListItems    2^41
            8796093022208,      // CompleteChores       2^43
            35184372088832,     // AddExpenses          2^45
            70368744177664,     // ManagePantry         2^46
            140737488355328,    // CreateDecisions      2^47
            281474976710656,    // VoteDecisions        2^48
        ];

        var fromMigration = literals.Aggregate(0ul, (mask, bit) => mask | bit);

        Assert.That((ulong)Role.HouseholdEveryonePermissions, Is.EqualTo(fromMigration),
            "the migration's UPDATE list and HouseholdEveryonePermissions must name the same bits");
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
