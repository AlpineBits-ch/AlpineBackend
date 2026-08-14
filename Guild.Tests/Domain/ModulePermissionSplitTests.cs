using Guild.Domain.Enums;

namespace Guild.Tests.Domain;

/// <summary>The invariants that make the core/module split safe, checked on the enums and the
/// feature map rather than through a service. These are the properties everything else assumes:
/// that no bit moved when it should not have, that the module mask is exhaustively owned, and that
/// switching a module off strips its bits and nothing else.</summary>
[TestFixture]
public class ModulePermissionSplitTests
{
    // ── The core mask kept its positions ──────────────────────────────────────

    /// <summary>
    /// Every core bit that existed before the split, pinned to the position it had.
    /// </summary>
    [TestCase(Permissions.ViewChannel, 0)]
    [TestCase(Permissions.SendMessages, 1)]
    [TestCase(Permissions.EditOwnMessages, 2)]
    [TestCase(Permissions.EditAnyMessage, 3)]
    [TestCase(Permissions.DeleteOwnMessages, 4)]
    [TestCase(Permissions.DeleteAnyMessage, 5)]
    [TestCase(Permissions.PinMessages, 6)]
    [TestCase(Permissions.AttachFiles, 7)]
    [TestCase(Permissions.EmbedLinks, 8)]
    [TestCase(Permissions.AddReactions, 9)]
    [TestCase(Permissions.Connect, 10)]
    [TestCase(Permissions.Speak, 11)]
    [TestCase(Permissions.Stream, 12)]
    [TestCase(Permissions.MuteMembers, 13)]
    [TestCase(Permissions.DeafenMembers, 14)]
    [TestCase(Permissions.MoveMembers, 15)]
    [TestCase(Permissions.CreateThreads, 16)]
    [TestCase(Permissions.SendMessagesInThreads, 17)]
    [TestCase(Permissions.ManageOwnThreads, 18)]
    [TestCase(Permissions.ManageAnyThread, 19)]
    [TestCase(Permissions.ManageChannel, 20)]
    [TestCase(Permissions.ManagePermissions, 21)]
    [TestCase(Permissions.CreateInvite, 22)]
    [TestCase(Permissions.KickMembers, 32)]
    [TestCase(Permissions.BanMembers, 33)]
    [TestCase(Permissions.ModerateMembers, 34)]
    [TestCase(Permissions.ManageGuild, 35)]
    [TestCase(Permissions.ViewAuditLog, 36)]
    [TestCase(Permissions.ManageEmojis, 37)]
    [TestCase(Permissions.ManageEvents, 38)]
    [TestCase(Permissions.MentionEveryone, 50)]
    [TestCase(Permissions.ManageRoles, 51)]
    [TestCase(Permissions.ManageWebhooks, 52)]
    [TestCase(Permissions.ChangeNickname, 53)]
    [TestCase(Permissions.ManageNicknames, 54)]
    [TestCase(Permissions.Superadmin, 63)]
    public void SurvivingCorePermissions_KeptTheirBitPositions(Permissions permission, int expectedBit)
    {
        Assert.That((ulong)permission, Is.EqualTo(1ul << expectedBit));
    }

    [Test]
    public void EveryCorePermission_OccupiesExactlyOneBit()
    {
        Assert.Multiple(() =>
        {
            foreach (var permission in Enum.GetValues<Permissions>())
            {
                if (permission == Permissions.None) continue;

                var value = (ulong)permission;
                Assert.That(value & (value - 1), Is.Zero,
                    $"{permission} is not a single bit - a composite value in a [Flags] enum breaks HasFlag");
            }
        });
    }

    [Test]
    public void EveryModulePermission_OccupiesExactlyOneBit()
    {
        Assert.Multiple(() =>
        {
            foreach (var permission in Enum.GetValues<ModulePermissions>())
            {
                if (permission == ModulePermissions.None) continue;

                var value = (ulong)permission;
                Assert.That(value & (value - 1), Is.Zero, $"{permission} is not a single bit");
            }
        });
    }

    [Test]
    public void NoTwoCorePermissions_ShareABit()
    {
        var values = Enum.GetValues<Permissions>()
            .Where(p => p != Permissions.None)
            .Select(p => (ulong)p)
            .ToList();

        Assert.That(values, Is.Unique);
    }

    [Test]
    public void NoTwoModulePermissions_ShareABit()
    {
        var values = Enum.GetValues<ModulePermissions>()
            .Where(p => p != ModulePermissions.None)
            .Select(p => (ulong)p)
            .ToList();

        Assert.That(values, Is.Unique);
    }

    // ── The feature map covers the module mask exhaustively ───────────────────

    [Test]
    public void EveryModulePermission_IsOwnedByAModule()
    {
        // The defining property of ModulePermissions: a bit in it that no module owns would never be
        // clamped, which makes it a core permission living in the wrong enum.
        var strippedWithNothingEnabled = GuildFeatureMap.DisabledModulePermissions(GuildFeatures.None);

        Assert.Multiple(() =>
        {
            foreach (var permission in Enum.GetValues<ModulePermissions>())
            {
                if (permission == ModulePermissions.None) continue;

                Assert.That(strippedWithNothingEnabled.HasFlag(permission), Is.True,
                    $"{permission} belongs to no GuildFeatures module - add it to GuildFeatureMap");
            }
        });
    }

    [Test]
    public void WithEveryModuleEnabled_NoModulePermissionIsStripped()
    {
        var everything = (GuildFeatures)ulong.MaxValue;

        Assert.That(GuildFeatureMap.DisabledModulePermissions(everything),
            Is.EqualTo(ModulePermissions.None));
    }

    // ── Clamping is per-mask ──────────────────────────────────────────────────

    [Test]
    public void DisablingAModule_StripsItsModuleBitsAndLeavesTheCoreMaskAlone()
    {
        // The round trip the whole split exists to make possible: a guild with the wiki off must
        // lose every wiki bit and keep every ordinary one.
        var withoutWiki = (GuildFeatures)ulong.MaxValue & ~GuildFeatures.Wiki;

        var core = Permissions.ViewChannel | Permissions.SendMessages | Permissions.ManageGuild;
        var module = ModulePermissions.ViewWiki | ModulePermissions.AddListItems;

        Assert.Multiple(() =>
        {
            Assert.That(GuildFeatureMap.ClampToEnabled(withoutWiki, core), Is.EqualTo(core),
                "no core bit belongs to the Wiki module, so none may be stripped");

            Assert.That(GuildFeatureMap.ClampToEnabled(withoutWiki, module),
                Is.EqualTo(ModulePermissions.AddListItems),
                "only the wiki bit goes; Lists is still on");
        });
    }

    [Test]
    public void DisablingAModule_MakesItsPermissionsUnavailable()
    {
        var withoutLists = (GuildFeatures)ulong.MaxValue & ~GuildFeatures.Lists;

        Assert.Multiple(() =>
        {
            Assert.That(GuildFeatureMap.IsPermissionAvailable(withoutLists, ModulePermissions.AddListItems),
                Is.False);
            Assert.That(GuildFeatureMap.IsPermissionAvailable(withoutLists, ModulePermissions.ViewWiki),
                Is.True, "an unrelated module is unaffected");
            Assert.That(GuildFeatureMap.IsPermissionAvailable(withoutLists, Permissions.SendMessages),
                Is.True, "and the core mask is untouched by a module being off");
        });
    }

    [Test]
    public void ClampingAnEmptyMask_YieldsNone()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GuildFeatureMap.ClampToEnabled(GuildFeatures.None, ModulePermissions.None),
                Is.EqualTo(ModulePermissions.None));
            Assert.That(GuildFeatureMap.ClampToEnabled(GuildFeatures.None, Permissions.None),
                Is.EqualTo(Permissions.None));
        });
    }

    // ── The new core bits sit in vacated space ────────────────────────────────

    [Test]
    public void TheNewParityBits_SitInTheRangesTheModuleBitsVacated()
    {
        // 23-31 and 39-41 were wiki/household bits.
        (Permissions Permission, int Bit)[] expected =
        [
            (Permissions.ReadMessageHistory, 23),
            (Permissions.SendVoiceMessages, 24),
            (Permissions.SendPolls, 25),
            (Permissions.UseExternalEmojis, 26),
            (Permissions.UseExternalStickers, 27),
            (Permissions.CreatePrivateThreads, 28),
            (Permissions.UseApplicationCommands, 29),
            (Permissions.CreateExpressions, 30),
            (Permissions.ManageExpressions, 31),
            (Permissions.PrioritySpeaker, 39),
            (Permissions.RequestToSpeak, 40),
            (Permissions.UseVoiceActivity, 41),
        ];

        Assert.Multiple(() =>
        {
            foreach (var (permission, bit) in expected)
            {
                Assert.That((ulong)permission, Is.EqualTo(1ul << bit), $"{permission} must be at bit {bit}");
            }
        });
    }

    [Test]
    public void CreateThreads_StillMeansPublicThreadsAtBitSixteen()
    {
        // The split added CreatePrivateThreads rather than renaming CreateThreads, so that no
        // stored mask changed meaning and the external contract kept its member name.
        Assert.Multiple(() =>
        {
            Assert.That((ulong)Permissions.CreateThreads, Is.EqualTo(1ul << 16));
            Assert.That(Permissions.CreatePrivateThreads, Is.Not.EqualTo(Permissions.CreateThreads));
        });
    }

    // ── The extension methods ─────────────────────────────────────────────────

    [Test]
    public void ModuleHasPermission_RequiresEveryRequestedBit()
    {
        var held = ModulePermissions.ViewWiki | ModulePermissions.CreateWikiPages;

        Assert.Multiple(() =>
        {
            Assert.That(held.HasPermission(ModulePermissions.ViewWiki), Is.True);
            Assert.That(held.HasPermission(ModulePermissions.ViewWiki | ModulePermissions.CreateWikiPages), Is.True);
            Assert.That(held.HasPermission(ModulePermissions.ViewWiki | ModulePermissions.DeleteWikiPages), Is.False,
                "a partial match is not a match");
        });
    }

    [Test]
    public void ModuleHasPermission_WithoutTheCoreMask_DoesNotHonourSuperadmin()
    {
        // There is no Superadmin bit in the module enum, so the single-argument overload cannot see
        // one.
        Assert.That(ModulePermissions.None.HasPermission(ModulePermissions.ViewWiki), Is.False);
    }

    [Test]
    public void ModuleHasPermission_WithTheCoreMask_HonoursSuperadmin()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ModulePermissions.None.HasPermission(ModulePermissions.ViewWiki, Permissions.Superadmin),
                Is.True);
            Assert.That(ModulePermissions.None.HasPermission(ModulePermissions.ViewWiki, Permissions.ManageGuild),
                Is.False, "ManageGuild is not Superadmin");
        });
    }
}
