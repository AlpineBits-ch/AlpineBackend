using System.Text.Json;
using Facet.Extensions;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>The guild half of the entitlement contract as a client actually receives it.</summary>
[TestFixture]
public class GuildFeatureResolutionWireTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";

    /// <summary>The number an unconstrained plan mask would be if it ever crossed the wire as one.
    /// The guild-side counterpart of long.MaxValue in EntitlementWireTests.</summary>
    private const string EveryBitSet = "18446744073709551615";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private FakeDistributedCache _cache = null!;
    private TestGuildContext _context = null!;
    private FakePlanFeatures _plan = null!;

    private sealed class FakePlanFeatures : IGuildPlanFeatures
    {
        public GuildFeatures Included { get; set; } = ~GuildFeatures.None;

        public Task<GuildFeatures> IncludedFeaturesAsync(
            string guildId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Included);
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

    private GuildPermissionService Planned() => new(
        _cache, _context, NullLogger<GuildPermissionService>.Instance, _plan);

    private GuildPermissionService Unplanned() => new(
        _cache, _context, NullLogger<GuildPermissionService>.Instance);

    private async Task SeedAsync(GuildFeatures features)
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "Test Guild", Features = features,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
    }

    // ══════════════════════════════════════════════════════════════════════════ Names, never the
    // mask ══════════════════════════════════════════════════════════════════════════

    /// <summary>The headline guarantee.</summary>
    [Test]
    public async Task NoMaskEverReachesTheWireAsANumber()
    {
        await SeedAsync(GuildFeaturePresets.Community);

        var dto = GuildFeatureResolutionDto.From(
            await Unplanned().GetGuildFeatureResolutionAsync(GuildId));

        var json = JsonSerializer.Serialize(dto, Web);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Not.Contain(EveryBitSet),
                "the unconstrained plan mask must never appear in the bytes");
            Assert.That(json, Does.Contain("\"Forums\""), "and the names must, instead");
            Assert.That(dto.IncludedByPlan, Is.EquivalentTo(AllFeatureNames()),
                "an unconstrained plan is every known module, listed");
        });
    }

    /// <summary>Every mask, not just the interesting one.</summary>
    [Test]
    public async Task EveryListIsNamesOnAGuildWithAPlan()
    {
        await SeedAsync(GuildFeaturePresets.Community);
        _plan.Included = GuildFeaturePresets.Community & ~GuildFeatures.Emojis;

        var json = JsonSerializer.Serialize(
            GuildFeatureResolutionDto.From(await Planned().GetGuildFeatureResolutionAsync(GuildId)),
            Web);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Not.Match(@"\d{4,}"),
                "nothing in this payload is a number, let alone a bitmask");
            Assert.That(json, Does.Contain("\"withheldByPlan\":[\"Emojis\"]"));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The four values a module screen branches on
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The case the whole shape exists for: one module the owner switched off and a different one
    /// the plan does not cover.
    /// </summary>
    [Test]
    public async Task OwnerDisabledAndPlanExcluded_AreFourDistinguishableValues()
    {
        await SeedAsync(GuildFeaturePresets.Community & ~GuildFeatures.Forums);
        _plan.Included = GuildFeaturePresets.Community & ~GuildFeatures.Emojis;

        var dto = GuildFeatureResolutionDto.From(
            await Planned().GetGuildFeatureResolutionAsync(GuildId));

        Assert.Multiple(() =>
        {
            Assert.That(dto.Chosen, Does.Not.Contain("Forums"));
            Assert.That(dto.Chosen, Does.Contain("Emojis"));

            Assert.That(dto.IncludedByPlan, Does.Contain("Forums"));
            Assert.That(dto.IncludedByPlan, Does.Not.Contain("Emojis"));

            Assert.That(dto.Effective, Does.Not.Contain("Forums"));
            Assert.That(dto.Effective, Does.Not.Contain("Emojis"));

            Assert.That(dto.WithheldByPlan, Is.EqualTo(new[] { "Emojis" }),
                "exactly the upgrade prompt: asked for, and not given");
        });
    }

    /// <summary>The normal case, and the one every guild is in today.</summary>
    [Test]
    public async Task APlanThatCoversTheGuild_WithholdsNothing()
    {
        await SeedAsync(GuildFeaturePresets.Community);
        _plan.Included = GuildFeaturePresets.Community;

        var dto = GuildFeatureResolutionDto.From(
            await Planned().GetGuildFeatureResolutionAsync(GuildId));

        Assert.Multiple(() =>
        {
            Assert.That(dto.WithheldByPlan, Is.Empty);
            Assert.That(dto.Effective, Is.EquivalentTo(dto.Chosen));
        });
    }

    /// <summary>The never-paywall floor, as a client sees it.</summary>
    [Test]
    public async Task ModerationAndVoiceAreNeverReportedAsWithheld()
    {
        await SeedAsync(GuildFeaturePresets.Community);
        _plan.Included = GuildFeatures.None;

        var dto = GuildFeatureResolutionDto.From(
            await Planned().GetGuildFeatureResolutionAsync(GuildId));

        Assert.Multiple(() =>
        {
            Assert.That(dto.Effective, Does.Contain("Moderation"));
            Assert.That(dto.Effective, Does.Contain("VoiceChannels"));
            Assert.That(dto.WithheldByPlan, Does.Not.Contain("Moderation"));
            Assert.That(dto.WithheldByPlan, Does.Not.Contain("VoiceChannels"));
            Assert.That(dto.WithheldByPlan, Does.Contain("Wiki"),
                "and everything the plan really is withholding is still reported");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Edges
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>An empty mask is four empty lists, not a missing field.</summary>
    [Test]
    public void AnEmptyMaskIsEmptyLists()
    {
        var dto = GuildFeatureResolutionDto.From(
            GuildFeatureMap.ResolveWithPlan(GuildFeatures.None, GuildFeatures.None));

        Assert.Multiple(() =>
        {
            Assert.That(dto.Chosen, Is.Empty);
            Assert.That(dto.IncludedByPlan, Is.Empty);
            Assert.That(dto.WithheldByPlan, Is.Empty);
            Assert.That(dto.Effective, Is.Empty);
            Assert.That(JsonSerializer.Serialize(dto, Web),
                Is.EqualTo("""{"chosen":[],"includedByPlan":[],"withheldByPlan":[],"effective":[]}"""));
        });
    }

    /// <summary>An unknown guild resolves to no modules rather than throwing, matching every other
    /// read on the permission service. The endpoint in front of this has already refused the
    /// caller.</summary>
    [Test]
    public async Task AnUnknownGuildIsNoModulesRatherThanAnError()
    {
        var dto = GuildFeatureResolutionDto.From(
            await Planned().GetGuildFeatureResolutionAsync("nope"));

        Assert.That(dto.Effective, Is.Empty);
    }

    /// <summary>Bits with no name in the enum are dropped rather than reported.</summary>
    [Test]
    public void UnnamedBitsAreDroppedRatherThanInvented()
    {
        var names = GuildFeatureMap.Names(~GuildFeatures.None);

        Assert.Multiple(() =>
        {
            Assert.That(names, Is.EquivalentTo(AllFeatureNames()));
            Assert.That(names, Has.Count.LessThan(64));
            Assert.That(names, Has.None.Matches<string>(name => name.StartsWith('-') || char.IsDigit(name[0])));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Additive: a v1 client sees exactly what it saw
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The resolution is an addition to <c>GuildDto</c> and nothing else changes.
    /// </summary>
    [Test]
    public void AGuildDtoWithNoResolutionIsUnchanged()
    {
        var guild = Guild.Domain.Aggregates.Guild.Create(new CreateGuildParams
        {
            Name = "The Flat", OwnerId = OwnerId, OwnerSearchValue = "OWNER",
            Kind = GuildKind.Household, SkipDefaultChannels = true,
        });

        var dto = guild.ToFacet<Guild.Domain.Aggregates.Guild, GuildDto>();

        Assert.Multiple(() =>
        {
            Assert.That(dto.FeatureResolution, Is.Null);
            Assert.That(JsonSerializer.Serialize(dto, Web), Does.Not.Contain("featureResolution"),
                "absent, not null - an added field that is written is not an additive change");
            Assert.That(dto.Features, Is.EqualTo(GuildFeaturePresets.Household),
                "and the existing mask still says what is effective");
        });
    }

    [Test]
    public void AGuildDtoWithAResolutionCarriesIt()
    {
        var guild = Guild.Domain.Aggregates.Guild.Create(new CreateGuildParams
        {
            Name = "The Flat", OwnerId = OwnerId, OwnerSearchValue = "OWNER",
            Kind = GuildKind.Household, SkipDefaultChannels = true,
        });

        var dto = guild.ToFacet<Guild.Domain.Aggregates.Guild, GuildDto>();
        dto.FeatureResolution = GuildFeatureResolutionDto.From(
            GuildFeatureMap.ResolveWithPlan(GuildFeaturePresets.Household, ~GuildFeatures.None));

        Assert.That(JsonSerializer.Serialize(dto, Web), Does.Contain("\"featureResolution\""));
    }

    private static IEnumerable<string> AllFeatureNames() =>
        Enum.GetValues<GuildFeatures>()
            .Where(feature => feature != GuildFeatures.None)
            .Select(feature => feature.ToString());
}
