using Echo.Entitlements.Model;
using Echo.Entitlements.Resolution;
using Guild.Application.Services;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>
/// The implementation behind <see cref="IGuildPlanFeatures"/>, which until now had none outside two
/// test fakes - so <c>GetGuildFeatureResolutionAsync</c> always took the "no plan source" branch,
/// every guild was reported as covered for every module, and <c>withheldByPlan</c> was permanently
/// empty by construction.
/// </summary>
[TestFixture]
public class PlanGuildFeaturesTests
{
    private const string GuildId = "guild-1";
    private const GuildFeatures Everything = ~GuildFeatures.None;

    private MemoryCache _cache = null!;

    [SetUp]
    public void SetUp() => _cache = new MemoryCache(new MemoryCacheOptions());

    [TearDown]
    public void TearDown() => _cache.Dispose();

    private PlanGuildFeatures Subject(IPlanAssignment assignment, params (string Plan, string Value)[] bundles) =>
        new(assignment,
            new GuildPlanFeatureBundles(bundles.ToDictionary(b => b.Plan, b => b.Value)),
            _cache,
            NullLogger<PlanGuildFeatures>.Instance);

    // ══════════════════════════════════════════════════════════════════════════
    // Absence means everything, in all four of the ways it can happen
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The shipped state of every instance: the pricing model tiers voice capacity, storage,
    /// emoji and bots, and deliberately tiers no module at all, so there is nothing configured here
    /// and nothing is withheld. The plan is not even resolved, which is what keeps a broker round trip
    /// off the permission path for a clamp that cannot withhold anything.</summary>
    [Test]
    public async Task NoBundlesConfigured_WithholdsNothingAndAsksNobody()
    {
        var assignment = new FakePlanAssignment("free");

        var included = await Subject(assignment).IncludedFeaturesAsync(GuildId);

        Assert.Multiple(() =>
        {
            Assert.That(included, Is.EqualTo(Everything));
            Assert.That(assignment.Calls, Is.Zero,
                "resolving a plan whose answer would be discarded is a broker call on every "
                + "permission check for nothing");
        });
    }

    /// <summary>Fail open, and only in this direction.</summary>
    [Test]
    public async Task AnUnreachablePlanSource_WithholdsNothing()
    {
        var assignment = new FakePlanAssignment("free") { Broken = true };

        var included = await Subject(assignment, ("free", "Wiki")).IncludedFeaturesAsync(GuildId);

        Assert.That(included, Is.EqualTo(Everything));
    }

    /// <summary>A guild on no plan is not a guild on the most restrictive one.</summary>
    [Test]
    public async Task AGuildOnNoPlan_WithholdsNothing()
    {
        var included = await Subject(new FakePlanAssignment(null), ("free", "Wiki"))
            .IncludedFeaturesAsync(GuildId);

        Assert.That(included, Is.EqualTo(Everything));
    }

    /// <summary>A plan nobody wrote a bundle for withholds nothing either.</summary>
    [Test]
    public async Task APlanWithNoBundle_WithholdsNothing()
    {
        var included = await Subject(new FakePlanAssignment("enterprise"), ("free", "Wiki"))
            .IncludedFeaturesAsync(GuildId);

        Assert.That(included, Is.EqualTo(Everything));
    }

    // ══════════════════════════════════════════════════════════════════════════ The clamp itself
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task AConfiguredBundle_CoversWhatItNamesAndNothingElse()
    {
        var included = await Subject(new FakePlanAssignment("free"), ("free", "Wiki, Events"))
            .IncludedFeaturesAsync(GuildId);

        Assert.Multiple(() =>
        {
            Assert.That(included.HasFlag(GuildFeatures.Wiki), Is.True);
            Assert.That(included.HasFlag(GuildFeatures.Events), Is.True);
            Assert.That(included.HasFlag(GuildFeatures.Forums), Is.False);
        });
    }

    [Test]
    public async Task AStarBundle_CoversEverything()
    {
        var included = await Subject(new FakePlanAssignment("pro"), ("free", "Wiki"), ("pro", "*"))
            .IncludedFeaturesAsync(GuildId);

        Assert.That(included, Is.EqualTo(Everything));
    }

    /// <summary>The list a plan may never withhold, whatever it is configured to cover.</summary>
    [Test]
    public async Task PlanIndependentFeatures_AreNeverWithheldByAPlan()
    {
        var included = await Subject(new FakePlanAssignment("free"), ("free", ""))
            .IncludedFeaturesAsync(GuildId);

        Assert.Multiple(() =>
        {
            Assert.That(included.HasFlag(GuildFeatureMap.PlanIndependentFeatures), Is.True);
            Assert.That(included.HasFlag(GuildFeatures.Moderation), Is.True);
            Assert.That(included.HasFlag(GuildFeatures.AutoMod), Is.True);
            Assert.That(included.HasFlag(GuildFeatures.VoiceChannels), Is.True);
        });
    }

    /// <summary>
    /// ManageGuild survives the emptiest plan there is, which is the single worst bug this
    /// programme could ship: a downgrade that takes away the screen where an owner would upgrade
    /// needs an upgrade to reach the screen that sells the upgrade.
    /// </summary>
    [Test]
    public async Task ManageGuild_SurvivesTheMostRestrictivePlanExpressible()
    {
        var included = await Subject(new FakePlanAssignment("free"), ("free", ""))
            .IncludedFeaturesAsync(GuildId);

        var effective = GuildFeatureMap.ClampToPlan(GuildFeaturePresets.Community, included);

        Assert.Multiple(() =>
        {
            Assert.That(
                GuildFeatureMap.IsPermissionAvailable(effective, Permissions.ManageGuild), Is.True,
                "the owner must be able to reach guild settings, billing and the upgrade path");
            Assert.That(
                GuildFeatureMap.DisabledPermissions(effective) & GuildFeatureMap.NeverGated,
                Is.EqualTo(Permissions.None));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Plan versions and unreadable configuration
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>A grandfathered subject's plan arrives as <c>pro@2</c>.</summary>
    [Test]
    public async Task APinnedPlanVersion_FallsBackToTheBundleForItsName()
    {
        var included = await Subject(new FakePlanAssignment("pro@2"), ("pro", "Wiki"))
            .IncludedFeaturesAsync(GuildId);

        Assert.That(included.HasFlag(GuildFeatures.Wiki), Is.True);
        Assert.That(included.HasFlag(GuildFeatures.Forums), Is.False);
    }

    /// <summary>And a bundle written against the exact version wins, so an operator can gate one
    /// version of a plan without naming every version of every other one.</summary>
    [Test]
    public async Task AnExactPlanVersion_BeatsTheBundleForItsName()
    {
        var included = await Subject(
                new FakePlanAssignment("pro@2"), ("pro", "Wiki"), ("pro@2", "Forums"))
            .IncludedFeaturesAsync(GuildId);

        Assert.Multiple(() =>
        {
            Assert.That(included.HasFlag(GuildFeatures.Forums), Is.True);
            Assert.That(included.HasFlag(GuildFeatures.Wiki), Is.False);
        });
    }

    /// <summary>A name this build does not know is skipped rather than thrown on: a bundle short one
    /// module still gates the rest, whereas refusing to start over one typo takes the service down
    /// for a value that fails open anyway.</summary>
    [Test]
    public async Task AnUnreadableModuleName_IsSkippedAndTheRestOfTheBundleApplies()
    {
        var included = await Subject(new FakePlanAssignment("free"), ("free", "Wiki,NotAModule,64"))
            .IncludedFeaturesAsync(GuildId);

        Assert.Multiple(() =>
        {
            Assert.That(included.HasFlag(GuildFeatures.Wiki), Is.True);
            Assert.That(included.HasFlag(GuildFeatures.Forums), Is.False,
                "and a bare number must not be read as a raw mask - that is the format this "
                + "configuration exists to keep out of an operator's hands");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Memoisation, because this runs on every message send
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ThePlanIsResolvedOncePerGuildWithinTheWindow()
    {
        var assignment = new FakePlanAssignment("free");
        var subject = Subject(assignment, ("free", "Wiki"));

        await subject.IncludedFeaturesAsync(GuildId);
        await subject.IncludedFeaturesAsync(GuildId);
        await subject.IncludedFeaturesAsync(GuildId);

        Assert.That(assignment.Calls, Is.EqualTo(1),
            "this is consulted on the permission path, which runs on every message send");
    }

    [Test]
    public async Task InvalidatingAGuild_SendsTheNextReadBackToThePlanSource()
    {
        var assignment = new FakePlanAssignment("free");
        var subject = Subject(assignment, ("free", "Wiki"), ("pro", "*"));

        Assert.That(await subject.IncludedFeaturesAsync(GuildId), Is.Not.EqualTo(Everything));

        assignment.Plan = "pro";
        subject.Invalidate(GuildId);

        Assert.That(await subject.IncludedFeaturesAsync(GuildId), Is.EqualTo(Everything));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // And the end the client actually reads
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The whole point of the package, through the service that answers <c>featureResolution</c>:
    /// <c>withheldByPlan</c> is exactly "the owner asked for this and is not getting it", and it is
    /// the only list that tells an out-of-plan module apart from an owner-disabled one.
    /// </summary>
    [Test]
    public async Task WithheldByPlan_NamesTheModulesTheOwnerAskedForAndIsNotGetting()
    {
        await using var context = new TestGuildContext(Guid.NewGuid().ToString());
        var cache = new FakeDistributedCache();

        context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = "owner-1", Name = "Test Guild",
            Features = GuildFeaturePresets.Community,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();

        var permissions = new GuildPermissionService(
            cache, context, NullLogger<GuildPermissionService>.Instance,
            Subject(new FakePlanAssignment("free"),
                ("free", string.Join(',', GuildFeatureMap.Names(GuildFeaturePresets.Community & ~GuildFeatures.Forums)))));

        var resolution = await permissions.GetGuildFeatureResolutionAsync(GuildId);

        Assert.Multiple(() =>
        {
            Assert.That(resolution.Chosen.HasFlag(GuildFeatures.Forums), Is.True,
                "the guild's own mask is untouched - the plan is never written into it");
            Assert.That(resolution.IncludedByPlan.HasFlag(GuildFeatures.Forums), Is.False);
            Assert.That(resolution.WithheldByPlan, Is.EqualTo(GuildFeatures.Forums));
            Assert.That(resolution.Effective.HasFlag(GuildFeatures.Forums), Is.False);
        });
    }

    /// <summary>What every guild looks like today, and must keep looking like until somebody
    /// configures a bundle: nothing withheld, and a payload identical to the one this service gave
    /// before the clamp had an implementation at all.</summary>
    [Test]
    public async Task WithNothingConfigured_TheResolutionIsWhatItAlwaysWas()
    {
        await using var context = new TestGuildContext(Guid.NewGuid().ToString());
        var cache = new FakeDistributedCache();

        context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = "owner-1", Name = "Test Guild",
            Features = GuildFeaturePresets.Community,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();

        var permissions = new GuildPermissionService(
            cache, context, NullLogger<GuildPermissionService>.Instance,
            Subject(new FakePlanAssignment("free")));

        var resolution = await permissions.GetGuildFeatureResolutionAsync(GuildId);

        Assert.Multiple(() =>
        {
            Assert.That(resolution.WithheldByPlan, Is.EqualTo(GuildFeatures.None));
            Assert.That(resolution.Effective, Is.EqualTo(GuildFeaturePresets.Community));
        });
    }

    /// <summary>The plan a guild is on, and whether asking for it works at all.</summary>
    private sealed class FakePlanAssignment(string? plan) : IPlanAssignment
    {
        public string? Plan { get; set; } = plan;

        /// <summary>Billing unreachable, which throws rather than answering "no plan" - the default
        /// is the answer to "this subject has no assignment", never to "we could not ask".</summary>
        public bool Broken { get; set; }

        public int Calls { get; private set; }

        public ValueTask<string?> PlanNameForAsync(
            EntitlementSubject subject, CancellationToken cancellationToken)
        {
            Calls++;

            if (Broken) throw new InvalidOperationException("billing is unreachable");

            return ValueTask.FromResult(Plan);
        }
    }
}
