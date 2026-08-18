using Guild.Domain.Enums;

namespace Guild.Tests.Domain;

/// <summary>
/// The roleplay modules and their permission bits, checked where it actually matters: that the new
/// bits are owned by a module and therefore clamped, and that the preset carries the two modules the
/// feature silently depends on.
/// </summary>
[TestFixture]
public class RoleplayModuleTests
{
    [TestCase(GuildFeatures.Personas, 12)]
    [TestCase(GuildFeatures.Scenes, 13)]
    [TestCase(GuildFeatures.Dice, 14)]
    [TestCase(GuildFeatures.Chronicle, 15)]
    public void TheRoleplayFeatures_SitInTheFreeRange(GuildFeatures feature, int bit) =>
        Assert.That((ulong)feature, Is.EqualTo(1ul << bit));

    [TestCase(ModulePermissions.UsePersonas, 24)]
    [TestCase(ModulePermissions.ManageAnyPersona, 25)]
    [TestCase(ModulePermissions.ApprovePersonas, 26)]
    [TestCase(ModulePermissions.ManageScenes, 27)]
    [TestCase(ModulePermissions.RollDice, 28)]
    [TestCase(ModulePermissions.RollHidden, 29)]
    [TestCase(ModulePermissions.ExportChronicle, 30)]
    public void TheRoleplayModulePermissions_WereAddedAtBitTwentyFourOrAbove(
        ModulePermissions permission, int bit) =>
        Assert.That((ulong)permission, Is.EqualTo(1ul << bit));

    /// <summary>The line item the whole exercise turns on: a bit no module owns is never clamped,
    /// so it stays granted with its module switched off.</summary>
    [Test]
    public void EveryRoleplayModulePermission_IsClampedWhenItsModuleIsOff()
    {
        (GuildFeatures Feature, ModulePermissions Permission)[] owned =
        [
            (GuildFeatures.Personas, ModulePermissions.UsePersonas),
            (GuildFeatures.Personas, ModulePermissions.ManageAnyPersona),
            (GuildFeatures.Personas, ModulePermissions.ApprovePersonas),
            (GuildFeatures.Scenes, ModulePermissions.ManageScenes),
            (GuildFeatures.Dice, ModulePermissions.RollDice),
            (GuildFeatures.Dice, ModulePermissions.RollHidden),
            (GuildFeatures.Chronicle, ModulePermissions.ExportChronicle),
        ];

        Assert.Multiple(() =>
        {
            foreach (var (feature, permission) in owned)
            {
                var without = (GuildFeatures)ulong.MaxValue & ~feature;

                Assert.That(GuildFeatureMap.ClampToEnabled(without, permission),
                    Is.EqualTo(ModulePermissions.None),
                    $"{permission} survived {feature} being off - add it to ModulePermissionOwners");

                Assert.That(GuildFeatureMap.IsPermissionAvailable(without, permission), Is.False);
            }
        });
    }

    [Test]
    public void SwitchingOffOneRoleplayModule_LeavesTheOthersAlone()
    {
        // Built from the bits rather than from the preset, so this keeps testing the clamp when the
        // preset changes - which it does as the unimplemented modules ship.
        var withoutDice = GuildFeatures.Personas | GuildFeatures.Chronicle;

        var requested = ModulePermissions.UsePersonas | ModulePermissions.RollDice |
                        ModulePermissions.ExportChronicle;

        Assert.That(GuildFeatureMap.ClampToEnabled(withoutDice, requested),
            Is.EqualTo(ModulePermissions.UsePersonas | ModulePermissions.ExportChronicle));
    }

    /// <summary>GuildFeatures has no dependency mechanism, so "Personas requires Wiki" can only be
    /// enforced where the feature set is written - which is here.</summary>
    [Test]
    public void TheRoleplayPreset_CarriesTheModulesTheFeatureSilentlyDependsOn()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GuildFeaturePresets.Roleplay.HasFlag(GuildFeatures.Wiki), Is.True,
                "character pages are wiki pages");
            Assert.That(GuildFeaturePresets.Roleplay.HasFlag(GuildFeatures.Presence), Is.True,
                "every AbsenceEndpoint route gates on Presence, and a stale-turn nudge respects absences");
            Assert.That(GuildFeaturePresets.Roleplay.HasFlag(GuildFeatures.Threads), Is.True);
            Assert.That(GuildFeaturePresets.Roleplay.HasFlag(GuildFeatures.Forums), Is.True);
            Assert.That(GuildFeaturePresets.Roleplay.HasFlag(GuildFeatures.Events), Is.True);
            Assert.That(GuildFeaturePresets.Roleplay.HasFlag(GuildFeatures.Moderation), Is.True);
        });
    }

    /// <summary>A feature flag is what a client gates its UI on, so the preset must not advertise a
    /// module with no implementation behind it. Each joins the preset when it ships.</summary>
    [Test]
    public void TheRoleplayPreset_CarriesOnlyTheRoleplayModulesThatExist()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GuildFeaturePresets.Roleplay.HasFlag(GuildFeatures.Personas), Is.True);
            Assert.That(GuildFeaturePresets.Roleplay.HasFlag(GuildFeatures.Scenes), Is.True);
            Assert.That(GuildFeaturePresets.Roleplay.HasFlag(GuildFeatures.Dice), Is.True);
            Assert.That(GuildFeaturePresets.Roleplay.HasFlag(GuildFeatures.Chronicle), Is.False,
                "nothing implements the chronicle yet, and a feature flag is what a client gates its UI on");
        });
    }

    [Test]
    public void RoleplayKind_SeedsTheRoleplayPreset() =>
        Assert.That(GuildFeaturePresets.For(GuildKind.Roleplay),
            Is.EqualTo(GuildFeaturePresets.Roleplay));

    /// <summary>Every other kind's preset is unchanged by the new bits: a household does not get
    /// dice.</summary>
    [Test]
    public void TheOtherPresets_DidNotPickUpTheRoleplayModules()
    {
        var roleplay = GuildFeatures.Personas | GuildFeatures.Scenes | GuildFeatures.Dice |
                       GuildFeatures.Chronicle;

        Assert.Multiple(() =>
        {
            foreach (var kind in Enum.GetValues<GuildKind>())
            {
                if (kind == GuildKind.Roleplay) continue;

                Assert.That(GuildFeaturePresets.For(kind) & roleplay, Is.EqualTo(GuildFeatures.None),
                    $"{kind} switched on a roleplay module");
            }
        });
    }

    /// <summary>ManageOwnPersonas is deliberately absent: editing your own global persona is not a
    /// per-guild capability, and a user in zero roleplay guilds holds no guild mask at all.</summary>
    [Test]
    public void ThereIsNoManageOwnPersonasBit() =>
        Assert.That(Enum.GetNames<ModulePermissions>(), Does.Not.Contain("ManageOwnPersonas"));

    /// <summary>@everyone holds UsePersonas, which looks like a widening and is not one: the clamp
    /// below refuses it wherever the module is off, and the constant is only read when the role is
    /// first created, so no existing guild's stored mask moves.</summary>
    [Test]
    public void OnlyARoleplayGuildGrantsUsePersonasToEveryone()
    {
        Assert.Multiple(() =>
        {
            // Not in the shared default: RemapModulePermissionBits is asserted to reproduce that
            // constant exactly, and a historical migration cannot produce a bit invented after it.
            Assert.That(
                Guild.Domain.Aggregates.Role.DefaultEveryoneModulePermissions
                    .HasFlag(ModulePermissions.UsePersonas),
                Is.False);

            Assert.That(
                Guild.Domain.Aggregates.Role.CreateEveryoneRole("guild-1", "member-1", GuildKind.Roleplay)
                    .ModulePermissions.HasFlag(ModulePermissions.UsePersonas),
                Is.True,
                "speaking as a character is the point of the guild kind, so it cannot need granting first");

            Assert.That(
                Guild.Domain.Aggregates.Role.CreateEveryoneRole("guild-1", "member-1", GuildKind.Community)
                    .ModulePermissions.HasFlag(ModulePermissions.UsePersonas),
                Is.False);
        });
    }
}
