using Facet.Extensions;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>
/// Covers the guild feature gate: a module that is switched off strips its permissions for
/// everybody, at the single choke point in GuildPermissionService.
/// </summary>
[TestFixture]
public class GuildFeatureGateTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string ChannelId = "channel-1";
    private const string RoleId = "role-1";
    private const string MemberId = "member-1";

    private FakeDistributedCache _cache = null!;
    private TestGuildContext _context = null!;
    private GuildPermissionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _service = new GuildPermissionService(
            _cache, _context, NullLogger<GuildPermissionService>.Instance);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static Guild.Domain.Aggregates.Guild MakeGuild(
        GuildFeatures features, string ownerId = OwnerId) => new()
    {
        Id = GuildId, OwnerId = ownerId, Name = "Test Guild", Features = features,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private async Task SeedAsync(GuildFeatures features, Permissions rolePermissions,
        string ownerId = OwnerId)
    {
        _context.Guilds.Add(MakeGuild(features, ownerId));
        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "test-channel", Description = "desc",
            Type = ChannelType.Text,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Roles.Add(new Role
        {
            Id = RoleId, GuildId = GuildId, Type = RoleType.None, Name = "test-role",
            Permissions = rolePermissions,
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

    // ══════════════════════════════════════════════════════════════════════════ The gate itself
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DisabledFeature_StripsItsPermissions_OnGuild()
    {
        await SeedAsync(GuildFeaturePresets.Community & ~GuildFeatures.Moderation,
            Permissions.BanMembers | Permissions.ManageGuild);

        Assert.Multiple(async () =>
        {
            Assert.That(await _service.CanUserPerformActionOnGuildAsync(
                UserId, GuildId, Permissions.BanMembers), Is.False,
                "BanMembers belongs to the Moderation module, which is off");

            Assert.That(await _service.CanUserPerformActionOnGuildAsync(
                UserId, GuildId, Permissions.ManageGuild), Is.True,
                "ManageGuild is never gated - it is how a feature gets switched back on");
        });
    }

    [Test]
    public async Task EnabledFeature_LeavesItsPermissionsAlone()
    {
        await SeedAsync(GuildFeaturePresets.Community, Permissions.BanMembers);

        Assert.That(await _service.CanUserPerformActionOnGuildAsync(
            UserId, GuildId, Permissions.BanMembers), Is.True);
    }

    [Test]
    public async Task DisabledFeature_StripsItsPermissions_OnChannel()
    {
        await SeedAsync(GuildFeaturePresets.Community & ~GuildFeatures.VoiceChannels,
            Permissions.ViewChannel | Permissions.Connect | Permissions.Speak);

        Assert.Multiple(async () =>
        {
            Assert.That(await _service.CanUserPerformActionAsync(
                UserId, ChannelId, Permissions.Connect), Is.False);

            Assert.That(await _service.CanUserPerformActionAsync(
                UserId, ChannelId, Permissions.ViewChannel), Is.True,
                "ViewChannel is core, not part of any module");
        });
    }

    /// <summary>The reason the gate runs before the owner short-circuit rather than inside the
    /// permission bitmask - see the class comment.</summary>
    [Test]
    public async Task GuildOwner_CannotUseDisabledFeature()
    {
        await SeedAsync(GuildFeaturePresets.Community & ~GuildFeatures.Wiki,
            Permissions.None, ownerId: UserId);

        Assert.Multiple(async () =>
        {
            Assert.That(await _service.CanUserPerformActionOnGuildAsync(
                UserId, GuildId, Permissions.CreateWikiPages), Is.False,
                "owner is not above a module the guild does not have");

            Assert.That(await _service.CanUserPerformActionAsync(
                UserId, ChannelId, Permissions.CreateWikiPages), Is.False);

            Assert.That(await _service.CanUserPerformActionOnGuildAsync(
                UserId, GuildId, Permissions.ManageGuild), Is.True,
                "the owner must still be able to turn the module back on");
        });
    }

    [Test]
    public async Task HouseholdPreset_HasNoModerationOrForumPermissions()
    {
        await SeedAsync(GuildFeaturePresets.Household,
            Permissions.BanMembers | Permissions.KickMembers | Permissions.Connect);

        Assert.Multiple(async () =>
        {
            Assert.That(await _service.CanUserPerformActionOnGuildAsync(
                UserId, GuildId, Permissions.BanMembers), Is.False);
            Assert.That(await _service.CanUserPerformActionOnGuildAsync(
                UserId, GuildId, Permissions.KickMembers), Is.False);
            Assert.That(await _service.CanUserPerformActionAsync(
                UserId, ChannelId, Permissions.Connect), Is.True,
                "households keep voice");
        });
    }

    [Test]
    public async Task FeaturesCache_IsInvalidatedExplicitly()
    {
        await SeedAsync(GuildFeaturePresets.Community, Permissions.BanMembers);

        Assert.That(await _service.CanUserPerformActionOnGuildAsync(
            UserId, GuildId, Permissions.BanMembers), Is.True);

        var guild = await _context.Guilds.FindAsync(GuildId);
        guild!.Features = GuildFeaturePresets.Community & ~GuildFeatures.Moderation;
        await _context.SaveChangesAsync();

        Assert.That(await _service.CanUserPerformActionOnGuildAsync(
            UserId, GuildId, Permissions.BanMembers), Is.True,
            "still served from the 15-minute feature cache until invalidated");

        await _service.InvalidateGuildFeaturesCacheAsync(GuildId);

        Assert.That(await _service.CanUserPerformActionOnGuildAsync(
            UserId, GuildId, Permissions.BanMembers), Is.False);
    }

    [Test]
    public async Task UnknownGuild_ResolvesToNoFeatures()
    {
        Assert.That(await _service.GetGuildFeaturesAsync("nope"), Is.EqualTo(GuildFeatures.None));
    }

    [Test]
    public async Task ClampToGrantable_DropsPermissionsOfDisabledModules()
    {
        // Owner holds every bit, so without the feature clamp an install would come away with
        // permissions for modules the guild does not have.
        await SeedAsync(GuildFeaturePresets.Community & ~GuildFeatures.Emojis,
            Permissions.None, ownerId: UserId);

        var clamped = await _service.ClampToGrantableAsync(
            UserId, GuildId, Permissions.ManageEmojis | Permissions.SendMessages);

        Assert.Multiple(() =>
        {
            Assert.That(clamped.HasFlag(Permissions.ManageEmojis), Is.False);
            Assert.That(clamped.HasFlag(Permissions.SendMessages), Is.True);
        });
    }

    [Test]
    public async Task IsFeatureEnabled_ReadsTheGuildRow()
    {
        await SeedAsync(GuildFeaturePresets.Household, Permissions.None);

        Assert.Multiple(async () =>
        {
            Assert.That(await _service.IsFeatureEnabledAsync(GuildId, GuildFeatures.Lists), Is.True);
            Assert.That(await _service.IsFeatureEnabledAsync(GuildId, GuildFeatures.Forums), Is.False);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Presets, seeding
    // and the map ══════════════════════════════════════════════════════════════════════════

    /// <summary>The migration backfills existing guilds with the literal 4095. If the Community
    /// preset ever gains a flag, that literal is silently wrong for every row already in the
    /// database - this is the test that catches it.</summary>
    [Test]
    public void CommunityPreset_MatchesTheMigrationBackfillLiteral()
    {
        Assert.That((ulong)GuildFeaturePresets.Community, Is.EqualTo(4095ul));
    }

    /// <summary>Community is the pre-gate behaviour, so it must not strip anything that existed
    /// before feature gating landed. It legitimately does strip the household-module permissions -
    /// those never existed for a community guild - so the invariant is stated against the
    /// community-scale modules rather than against "nothing at all".</summary>
    [Test]
    public void CommunityPreset_StripsNoPreExistingPermission()
    {
        var householdOnly =
            Permissions.ManageLists | Permissions.AddListItems | Permissions.CheckOffListItems |
            Permissions.ManageChores | Permissions.CompleteChores |
            Permissions.ManageLedger | Permissions.AddExpenses |
            Permissions.ManagePantry |
            Permissions.CreateDecisions | Permissions.VoteDecisions |
            Permissions.ManageGuests;

        var stripped = GuildFeatureMap.DisabledPermissions(GuildFeaturePresets.Community);

        Assert.That(stripped & ~householdOnly, Is.EqualTo(Permissions.None),
            "every guild that existed before gating must keep every permission it had");
    }

    [Test]
    public void HouseholdPreset_EnablesEveryHouseholdPermission()
    {
        var stripped = GuildFeatureMap.DisabledPermissions(GuildFeaturePresets.Household);

        Assert.Multiple(() =>
        {
            Assert.That(stripped.HasFlag(Permissions.AddListItems), Is.False);
            Assert.That(stripped.HasFlag(Permissions.CompleteChores), Is.False);
            Assert.That(stripped.HasFlag(Permissions.AddExpenses), Is.False);
            Assert.That(stripped.HasFlag(Permissions.ManagePantry), Is.False);
            Assert.That(stripped.HasFlag(Permissions.VoteDecisions), Is.False);
            Assert.That(stripped.HasFlag(Permissions.ManageGuests), Is.False);
            Assert.That(stripped.HasFlag(Permissions.BanMembers), Is.True,
                "households don't ban each other");
        });
    }

    [Test]
    public void Create_SeedsFeaturesFromKind()
    {
        var guild = Guild.Domain.Aggregates.Guild.Create(new CreateGuildParams
        {
            Name = "The Flat", OwnerId = OwnerId, OwnerSearchValue = "OWNER",
            Kind = GuildKind.Household,
        });

        Assert.Multiple(() =>
        {
            Assert.That(guild.Kind, Is.EqualTo(GuildKind.Household));
            Assert.That(guild.Features, Is.EqualTo(GuildFeaturePresets.Household));
        });
    }

    [Test]
    public void Create_DefaultsToCommunity()
    {
        var guild = Guild.Domain.Aggregates.Guild.Create(new CreateGuildParams
        {
            Name = "A Server", OwnerId = OwnerId, OwnerSearchValue = "OWNER",
        });

        Assert.Multiple(() =>
        {
            Assert.That(guild.Kind, Is.EqualTo(GuildKind.Community));
            Assert.That(guild.Features, Is.EqualTo(GuildFeaturePresets.Community));
        });
    }

    /// <summary>Templates captured before feature gating existed deserialize with Features == None.
    /// That has to mean "take the preset", not "a guild with every module switched off".</summary>
    [Test]
    public void Create_TreatsReplayedNoneAsPreset()
    {
        var guild = Guild.Domain.Aggregates.Guild.Create(new CreateGuildParams
        {
            Name = "From Old Template", OwnerId = OwnerId, OwnerSearchValue = "OWNER",
            Kind = GuildKind.Community, Features = GuildFeatures.None,
        });

        Assert.That(guild.Features, Is.EqualTo(GuildFeaturePresets.Community));
    }

    [Test]
    public void Create_HonoursExplicitlyReplayedFeatures()
    {
        var replayed = GuildFeatures.Lists | GuildFeatures.Wiki;

        var guild = Guild.Domain.Aggregates.Guild.Create(new CreateGuildParams
        {
            Name = "From Template", OwnerId = OwnerId, OwnerSearchValue = "OWNER",
            Kind = GuildKind.Household, Features = replayed,
        });

        Assert.That(guild.Features, Is.EqualTo(replayed),
            "an explicitly captured feature set must survive the preset");
    }

    /// <summary>GuildDto is a [Facet] of the aggregate, so Kind/Features reach clients without any
    /// DTO code - which also means nothing would fail loudly if the facet stopped projecting them.
    /// This is that failure.</summary>
    [Test]
    public void GuildDto_ProjectsKindAndFeatures()
    {
        // SkipDefaultChannels because GuildDto's nested ChannelDto facet dereferences
        // Channel.Guild, which is only populated once EF has tracked the graph - not on a
        // freshly constructed aggregate. Irrelevant to what this test asserts.
        var guild = Guild.Domain.Aggregates.Guild.Create(new CreateGuildParams
        {
            Name = "The Flat", OwnerId = OwnerId, OwnerSearchValue = "OWNER",
            Kind = GuildKind.Household, SkipDefaultChannels = true,
        });

        var dto = guild.ToFacet<Guild.Domain.Aggregates.Guild, GuildDto>();

        Assert.Multiple(() =>
        {
            Assert.That(dto.Kind, Is.EqualTo(GuildKind.Household));
            Assert.That(dto.Features, Is.EqualTo(GuildFeaturePresets.Household));
        });
    }

    [Test]
    public void RequiredFeatureFor_MapsChannelTypes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GuildFeatureMap.RequiredFeatureFor(ChannelType.Text), Is.Null,
                "text channels are the platform, not a module");
            Assert.That(GuildFeatureMap.RequiredFeatureFor(ChannelType.Voice),
                Is.EqualTo(GuildFeatures.VoiceChannels));
            Assert.That(GuildFeatureMap.RequiredFeatureFor(ChannelType.Forum),
                Is.EqualTo(GuildFeatures.Forums));
            Assert.That(GuildFeatureMap.RequiredFeatureFor(ChannelType.Media),
                Is.EqualTo(GuildFeatures.Forums),
                "Media is a Forum in every respect this service cares about");
            Assert.That(GuildFeatureMap.RequiredFeatureFor(ChannelType.Announcement),
                Is.EqualTo(GuildFeatures.Announcements));
            Assert.That(GuildFeatureMap.RequiredFeatureFor(ChannelType.Thread),
                Is.EqualTo(GuildFeatures.Threads));
        });
    }
}
