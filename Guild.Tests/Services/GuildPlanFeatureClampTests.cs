using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>
/// Covers the plan clamp: a guild's modules are what its owner switched on intersected with what
/// its plan covers, and a module outside the plan loses its permissions through the same
/// DisabledPermissions path a switched-off module does.
/// </summary>
[TestFixture]
public class GuildPlanFeatureClampTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string ChannelId = "channel-1";
    private const string RoleId = "role-1";
    private const string MemberId = "member-1";

    private FakeDistributedCache _cache = null!;
    private TestGuildContext _context = null!;
    private FakePlanFeatures _plan = null!;

    /// <summary>A plan whose covered module set the test can move, which is what a subscription
    /// change, a lapse and an admin grant all look like from this side of the seam.</summary>
    private sealed class FakePlanFeatures : IGuildPlanFeatures
    {
        public GuildFeatures Included { get; set; } = ~GuildFeatures.None;

        public int Calls { get; private set; }

        public Task<GuildFeatures> IncludedFeaturesAsync(
            string guildId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Included);
        }
    }

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _plan = new FakePlanFeatures();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <summary>With a plan source.</summary>
    private GuildPermissionService Planned() => new(
        _cache, _context, NullLogger<GuildPermissionService>.Instance, _plan);

    private GuildPermissionService Unplanned() => new(
        _cache, _context, NullLogger<GuildPermissionService>.Instance);

    private async Task SeedAsync(
        GuildFeatures features,
        Permissions rolePermissions,
        ModulePermissions roleModulePermissions = ModulePermissions.None,
        string ownerId = OwnerId)
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = ownerId, Name = "Test Guild", Features = features,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "test-channel", Description = "desc",
            Type = ChannelType.Text,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Roles.Add(new Role
        {
            Id = RoleId, GuildId = GuildId, Type = RoleType.None, Name = "test-role",
            Permissions = rolePermissions, ModulePermissions = roleModulePermissions,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            SearchValue = "USER-1",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-1", RoleId = RoleId, MemberId = MemberId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
    }

    // ══════════════════════════════════════════════════════════════════════════ No regression for
    // anyone unpaid ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task NoPlanSource_ChangesNothing()
    {
        await SeedAsync(GuildFeaturePresets.Community, Permissions.BanMembers | Permissions.ManageGuild);

        var service = Unplanned();

        Assert.Multiple(async () =>
        {
            Assert.That(await service.GetGuildFeaturesAsync(GuildId),
                Is.EqualTo(GuildFeaturePresets.Community),
                "an absent plan source must mean 'the plan covers everything', never 'nothing'");
            Assert.That(await service.CanUserPerformActionOnGuildAsync(
                UserId, GuildId, Permissions.BanMembers), Is.True);
        });
    }

    [Test]
    public async Task FeatureInThePlan_BehavesExactlyAsToday()
    {
        await SeedAsync(GuildFeaturePresets.Community,
            Permissions.BanMembers | Permissions.Connect | Permissions.ManageEmojis);
        _plan.Included = GuildFeaturePresets.Community;

        var planned = Planned();
        var unplanned = Unplanned();

        Assert.Multiple(async () =>
        {
            Assert.That(await planned.GetGuildFeaturesAsync(GuildId),
                Is.EqualTo(await unplanned.GetGuildFeaturesAsync(GuildId)));
            Assert.That(await planned.CanUserPerformActionOnGuildAsync(
                UserId, GuildId, Permissions.BanMembers), Is.True);
            Assert.That(await planned.CanUserPerformActionOnGuildAsync(
                UserId, GuildId, Permissions.ManageEmojis), Is.True);
            Assert.That(await planned.CanUserPerformActionAsync(
                UserId, ChannelId, Permissions.Connect), Is.True);
        });
    }

    /// <summary>A plan that covers more than the guild uses cannot switch anything on.</summary>
    [Test]
    public async Task PlanCoveringMoreThanTheGuildUses_EnablesNothingExtra()
    {
        await SeedAsync(GuildFeaturePresets.Community & ~GuildFeatures.Wiki,
            Permissions.None, ModulePermissions.CreateWikiPages);
        _plan.Included = ~GuildFeatures.None;

        Assert.That(await Planned().CanUserPerformActionOnGuildAsync(
            UserId, GuildId, ModulePermissions.CreateWikiPages), Is.False,
            "the plan covers Wiki, but the owner switched it off and that is the owner's call");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // A feature outside the plan, and the path it is stripped through
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task FeatureOutsideThePlan_StripsItsPermissions()
    {
        await SeedAsync(GuildFeaturePresets.Community, Permissions.ManageEmojis);
        _plan.Included = GuildFeaturePresets.Community & ~GuildFeatures.Emojis;

        Assert.That(await Planned().CanUserPerformActionOnGuildAsync(
            UserId, GuildId, Permissions.ManageEmojis), Is.False);
    }

    /// <summary>The mechanism, not just the outcome.</summary>
    [Test]
    public async Task FeatureOutsideThePlan_IsStrippedThroughTheExistingDisabledPermissionsPath()
    {
        await SeedAsync(GuildFeaturePresets.Community, Permissions.ManageEmojis);
        _plan.Included = GuildFeaturePresets.Community & ~GuildFeatures.Emojis;

        var service = Planned();
        var resolution = await service.GetGuildFeatureResolutionAsync(GuildId);
        var effective = await service.GetGuildFeaturesAsync(GuildId);

        Assert.Multiple(async () =>
        {
            Assert.That(resolution.Chosen.HasFlag(GuildFeatures.Emojis), Is.True,
                "the guild's own mask is untouched - the plan is not written into it");
            Assert.That(effective.HasFlag(GuildFeatures.Emojis), Is.False);
            Assert.That(GuildFeatureMap.DisabledPermissions(effective).HasFlag(Permissions.ManageEmojis),
                Is.True, "stripped by the same table a disabled module is stripped by");
            Assert.That(GuildFeatureMap.IsPermissionAvailable(effective, Permissions.ManageEmojis),
                Is.False);
            Assert.That((await service.GetGuildPermissionsAsync(OwnerId, GuildId))
                .HasFlag(Permissions.ManageEmojis), Is.False,
                "and the advertised mask agrees, through the same ClampToEnabled call as before");
        });
    }

    [Test]
    public async Task ModulePermissionsOutsideThePlan_AreStrippedTheSameWay()
    {
        await SeedAsync(GuildFeaturePresets.Household, Permissions.None,
            ModulePermissions.ManagePantry);
        _plan.Included = GuildFeaturePresets.Household & ~GuildFeatures.Pantry;

        var service = Planned();
        var effective = await service.GetGuildFeaturesAsync(GuildId);

        Assert.Multiple(async () =>
        {
            Assert.That(
                GuildFeatureMap.DisabledModulePermissions(effective).HasFlag(ModulePermissions.ManagePantry),
                Is.True);
            Assert.That(await service.CanUserPerformActionOnGuildAsync(
                UserId, GuildId, ModulePermissions.ManagePantry), Is.False);
            Assert.That(await service.IsFeatureEnabledAsync(GuildId, GuildFeatures.Pantry), Is.False,
                "the endpoints that gate on the feature directly see the clamp too");
        });
    }

    /// <summary>A guild owner is not above their own plan, for the same reason they are not above a
    /// module they switched off: this runs in front of the owner short-circuit, not inside the
    /// permission mask.</summary>
    [Test]
    public async Task GuildOwner_CannotUseAFeatureOutsideThePlan()
    {
        await SeedAsync(GuildFeaturePresets.Community, Permissions.None, ownerId: UserId);
        _plan.Included = GuildFeaturePresets.Community & ~GuildFeatures.Emojis;

        Assert.That(await Planned().CanUserPerformActionOnGuildAsync(
            UserId, GuildId, Permissions.ManageEmojis), Is.False);
    }

    /// <summary>An install cannot be handed permissions the plan does not cover either, because
    /// ClampToGrantableAsync clamps against the same effective mask.</summary>
    [Test]
    public async Task ClampToGrantable_DropsPermissionsOutsideThePlan()
    {
        await SeedAsync(GuildFeaturePresets.Community, Permissions.None, ownerId: UserId);
        _plan.Included = GuildFeaturePresets.Community & ~GuildFeatures.Emojis;

        var clamped = await Planned().ClampToGrantableAsync(
            UserId, GuildId, Permissions.ManageEmojis | Permissions.SendMessages);

        Assert.Multiple(() =>
        {
            Assert.That(clamped.HasFlag(Permissions.ManageEmojis), Is.False);
            Assert.That(clamped.HasFlag(Permissions.SendMessages), Is.True);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ The lockout that
    // must never happen ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A downgrade must never lock an owner out of the screen where they would upgrade.
    /// </summary>
    [Test]
    public async Task MostRestrictivePlan_LeavesTheOwnerAbleToReachSettingsAndUpgrade()
    {
        await SeedAsync(GuildFeaturePresets.Community, Permissions.None, ownerId: UserId);
        _plan.Included = GuildFeatures.None;

        var service = Planned();
        var advertised = await service.GetGuildPermissionsAsync(UserId, GuildId);

        Assert.Multiple(async () =>
        {
            Assert.That(await service.CanUserPerformActionOnGuildAsync(
                UserId, GuildId, Permissions.ManageGuild), Is.True,
                "the owner must be able to reach guild settings, billing and the upgrade path");
            Assert.That(advertised.HasFlag(Permissions.ManageGuild), Is.True,
                "and the client must be told so, or it will not render the screen");

            foreach (var capability in Ungated())
            {
                Assert.That(await service.CanUserPerformActionOnGuildAsync(
                    UserId, GuildId, capability), Is.True,
                    $"{capability} is ungated by design and no plan may reach it");
                Assert.That(await service.CanUserPerformActionAsync(
                    UserId, ChannelId, capability), Is.True,
                    $"{capability} is ungated by design on the channel path too");
                Assert.That(advertised.HasFlag(capability), Is.True);
            }
        });
    }

    /// <summary>The same guarantee for the administrator who is not the owner.</summary>
    [Test]
    public async Task MostRestrictivePlan_LeavesAManageGuildRoleHolderAbleToUpgrade()
    {
        await SeedAsync(GuildFeaturePresets.Community, Permissions.ManageGuild);
        _plan.Included = GuildFeatures.None;

        Assert.That(await Planned().CanUserPerformActionOnGuildAsync(
            UserId, GuildId, Permissions.ManageGuild), Is.True);
    }

    /// <summary>The structural reason the two tests above pass, asserted directly so it cannot rot
    /// silently: no module owns any ungated bit, so no feature mask - clamped, unclamped or empty -
    /// can strip one. A bit moved into GuildFeatureMap's owner tables would fail here rather than
    /// in production, which is the whole reason NeverGated is data.</summary>
    [Test]
    public void NoModuleOwnsAnUngatedCapability()
    {
        var strippedByEverythingOff = GuildFeatureMap.DisabledPermissions(GuildFeatures.None);

        Assert.Multiple(() =>
        {
            Assert.That(strippedByEverythingOff & GuildFeatureMap.NeverGated,
                Is.EqualTo(Permissions.None),
                "a bit that is ungated by design must belong to no module");

            foreach (var capability in Ungated())
            {
                Assert.That(
                    GuildFeatureMap.IsPermissionAvailable(
                        GuildFeatureMap.ClampToPlan(GuildFeaturePresets.Community, GuildFeatures.None),
                        capability),
                    Is.True, $"{capability} survives the most restrictive plan there is");
            }
        });
    }

    /// <summary>Moderation and voice are never a plan's to withhold - the monetization spec's
    /// never-paywall list. What a plan buys is voice capacity, enforced where a room is joined; a
    /// guild that cannot moderate itself, or cannot talk at all, is the platform's problem.
    /// </summary>
    [Test]
    public async Task PlanCannotWithholdModerationOrVoice()
    {
        await SeedAsync(GuildFeaturePresets.Community,
            Permissions.BanMembers | Permissions.ViewChannel | Permissions.Connect);
        _plan.Included = GuildFeatures.None;

        var service = Planned();

        Assert.Multiple(async () =>
        {
            Assert.That(await service.CanUserPerformActionOnGuildAsync(
                UserId, GuildId, Permissions.BanMembers), Is.True);
            Assert.That(await service.CanUserPerformActionAsync(
                UserId, ChannelId, Permissions.Connect), Is.True);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The two states that must stay distinguishable
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>"The owner switched this off" and "the plan does not cover this" produce the
    /// identical refusal at the choke point and have opposite remedies - a toggle versus an upgrade,
    /// both reachable only by the same person. If the only thing that reached a client were the
    /// clamped mask, an owner would be shown "you do not have permission" for something they are
    /// entitled to buy.</summary>
    [Test]
    public async Task OwnerDisabledAndPlanExcluded_AreDistinguishableFromOutside()
    {
        await SeedAsync(GuildFeaturePresets.Community & ~GuildFeatures.Forums, Permissions.None);
        _plan.Included = GuildFeaturePresets.Community & ~GuildFeatures.Emojis;

        var resolution = await Planned().GetGuildFeatureResolutionAsync(GuildId);

        Assert.Multiple(() =>
        {
            Assert.That(resolution.Chosen.HasFlag(GuildFeatures.Forums), Is.False);
            Assert.That(resolution.Chosen.HasFlag(GuildFeatures.Emojis), Is.True);

            Assert.That(resolution.IncludedByPlan.HasFlag(GuildFeatures.Forums), Is.True);
            Assert.That(resolution.IncludedByPlan.HasFlag(GuildFeatures.Emojis), Is.False);

            Assert.That(resolution.Effective.HasFlag(GuildFeatures.Forums), Is.False);
            Assert.That(resolution.Effective.HasFlag(GuildFeatures.Emojis), Is.False);

            Assert.That(resolution.WithheldByPlan, Is.EqualTo(GuildFeatures.Emojis),
                "exactly the set the owner asked for and is not being given, which is the upgrade "
                + "prompt and nothing else");
        });
    }

    [Test]
    public async Task NothingIsWithheldWhenThePlanCoversWhatTheGuildUses()
    {
        await SeedAsync(GuildFeaturePresets.Community, Permissions.None);
        _plan.Included = GuildFeaturePresets.Community;

        var resolution = await Planned().GetGuildFeatureResolutionAsync(GuildId);

        Assert.That(resolution.WithheldByPlan, Is.EqualTo(GuildFeatures.None));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Upgrade, downgrade, and whose choice survives
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The interaction case.</summary>
    [Test]
    public async Task OwnerDisabledFeature_StaysDisabledThroughAPlanUpgrade()
    {
        await SeedAsync(GuildFeaturePresets.Community & ~GuildFeatures.Wiki,
            Permissions.None, ModulePermissions.CreateWikiPages);
        _plan.Included = GuildFeaturePresets.Community & ~GuildFeatures.Wiki;

        var service = Planned();

        Assert.That(await service.CanUserPerformActionOnGuildAsync(
            UserId, GuildId, ModulePermissions.CreateWikiPages), Is.False);

        _plan.Included = ~GuildFeatures.None;

        var resolution = await service.GetGuildFeatureResolutionAsync(GuildId);

        Assert.Multiple(async () =>
        {
            Assert.That(await service.CanUserPerformActionOnGuildAsync(
                UserId, GuildId, ModulePermissions.CreateWikiPages), Is.False,
                "the owner's own choice must survive the upgrade");
            Assert.That(resolution.Chosen.HasFlag(GuildFeatures.Wiki), Is.False);
            Assert.That(resolution.WithheldByPlan.HasFlag(GuildFeatures.Wiki), Is.False,
                "and it must not be reported as something an upgrade would unlock, because it "
                + "would not");
        });
    }

    /// <summary>
    /// The other half: what the plan withheld does come back, without the owner touching anything.
    /// </summary>
    [Test]
    public async Task PlanWithheldFeature_ReturnsOnUpgradeWithoutTheOwnerActing()
    {
        await SeedAsync(GuildFeaturePresets.Community, Permissions.None,
            ModulePermissions.CreateWikiPages);
        _plan.Included = GuildFeaturePresets.Community & ~GuildFeatures.Wiki;

        var service = Planned();

        Assert.That(await service.CanUserPerformActionOnGuildAsync(
            UserId, GuildId, ModulePermissions.CreateWikiPages), Is.False);
        Assert.That((await service.GetGuildFeatureResolutionAsync(GuildId))
            .WithheldByPlan.HasFlag(GuildFeatures.Wiki), Is.True);

        _plan.Included = GuildFeaturePresets.Community;

        Assert.That(await service.CanUserPerformActionOnGuildAsync(
            UserId, GuildId, ModulePermissions.CreateWikiPages), Is.True);
    }

    /// <summary>The feature cache holds the guild's own mask and not the clamped one, so a plan
    /// change takes effect without an eviction per guild. Mixing the two would make a stale plan
    /// indistinguishable from a stale feature toggle and would need the plan's invalidation to
    /// reach a key it does not own.</summary>
    [Test]
    public async Task PlanChange_TakesEffectWithoutInvalidatingTheFeatureCache()
    {
        await SeedAsync(GuildFeaturePresets.Community, Permissions.ManageEmojis);
        _plan.Included = GuildFeaturePresets.Community;

        var service = Planned();

        Assert.That(await service.CanUserPerformActionOnGuildAsync(
            UserId, GuildId, Permissions.ManageEmojis), Is.True);

        _plan.Included = GuildFeaturePresets.Community & ~GuildFeatures.Emojis;

        Assert.Multiple(async () =>
        {
            Assert.That(await service.CanUserPerformActionOnGuildAsync(
                UserId, GuildId, Permissions.ManageEmojis), Is.False);
            Assert.That(_plan.Calls, Is.GreaterThan(1),
                "the plan is consulted per read rather than baked into the cached entry");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Edges
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UnknownGuild_StillResolvesToNoFeatures()
    {
        _plan.Included = ~GuildFeatures.None;

        Assert.That(await Planned().GetGuildFeaturesAsync("nope"), Is.EqualTo(GuildFeatures.None));
    }

    /// <summary>The plan source is an optional constructor parameter with no registration behind
    /// it, and the container has to honour the default rather than fail to build the service. A
    /// regression here would not be a failing test anywhere else - it would be every permission
    /// check in the service throwing at startup.</summary>
    [Test]
    public void ServiceResolves_WithNoPlanSourceRegistered()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDistributedCache>(_cache);
        services.AddSingleton<MicroserviceContext>(_context);
        services.AddSingleton<ILogger<GuildPermissionService>>(
            NullLogger<GuildPermissionService>.Instance);
        services.AddScoped<GuildPermissionService>();

        using var provider = services.BuildServiceProvider();

        Assert.That(provider.GetRequiredService<GuildPermissionService>(), Is.Not.Null);
    }

    private static IEnumerable<Permissions> Ungated() =>
        Enum.GetValues<Permissions>()
            .Where(permission => permission != Permissions.None
                                 && GuildFeatureMap.NeverGated.HasFlag(permission));
}
