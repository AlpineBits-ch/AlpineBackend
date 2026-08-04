using Identity.Contracts.Bus.Events;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Domain;
using Messaging.Application.Handler.Privacy;
using Messaging.Application.Services.Privacy;
using Messaging.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Contracts.Bus.Integration.Events;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;

namespace Messaging.Tests.Services;

/// <summary>
/// The two Redis-backed policy caches from §1.4 of docs/specs/privacy.md: key shape, batching,
/// eviction on the change events, and - the cases that matter - what they answer when the owning
/// service cannot be reached.
/// </summary>
[TestFixture]
public class PrivacyCacheTests
{
    private FakeDistributedCache _cache = null!;

    [SetUp]
    public void SetUp() => _cache = new FakeDistributedCache();

    private static UserPrivacySettingsSummary Settings(string userId, DirectMessagePolicy policy) => new()
    {
        UserId = userId,
        DirectMessagePolicy = policy,
        SendReadReceipts = true,
        SendTypingIndicators = true,
        Version = 3,
    };

    private PrivacySettingsCache PrivacyOver(FakeMessageBus bus) =>
        new(_cache, bus, NullLogger<PrivacySettingsCache>.Instance);

    private BlockCache BlocksOver(FakeMessageBus bus) =>
        new(_cache, bus, NullLogger<BlockCache>.Instance);

    // ── PrivacySettingsCache ──────────────────────────────────────────────────

    [Test]
    public async Task PrivacySettings_AreFetchedOnceAndThenServedFromTheCache()
    {
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetUserPrivacySettingsRequest => new GetUserPrivacySettingsResponse
            {
                Settings = [Settings("user-1", DirectMessagePolicy.Everyone)],
            },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var cache = PrivacyOver(bus);

        var first = await cache.GetAsync("user-1");
        var second = await cache.GetAsync("user-1");

        Assert.Multiple(() =>
        {
            Assert.That(first.DirectMessagePolicy, Is.EqualTo(DirectMessagePolicy.Everyone));
            Assert.That(second.DirectMessagePolicy, Is.EqualTo(DirectMessagePolicy.Everyone));
            Assert.That(bus.Invoked.OfType<GetUserPrivacySettingsRequest>().Count(), Is.EqualTo(1),
                "The second read must come out of Redis, not off the bus");
            Assert.That(_cache.HasEntry(PrivacySettingsCache.KeyFor("user-1")), Is.True);
        });
    }

    [Test]
    public async Task PrivacySettings_KeyIsTheOneEverySericeAgreesOn()
    {
        // Hard-coded rather than derived from the helper: the whole point of the key shape is that
        // an eviction written by another service lands on the same string.
        Assert.That(PrivacySettingsCache.KeyFor("usr_abc"), Is.EqualTo("privacy_settings:user_id:usr_abc"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task PrivacySettings_AreBatchedIntoOneRequest()
    {
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetUserPrivacySettingsRequest r => new GetUserPrivacySettingsResponse
            {
                Settings = r.UserIds.Select(id => Settings(id, DirectMessagePolicy.Everyone)).ToList(),
            },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await PrivacyOver(bus).GetAsync(["user-1", "user-2", "user-3"]);

        Assert.Multiple(() =>
        {
            Assert.That(result, Has.Count.EqualTo(3));
            Assert.That(bus.Invoked.OfType<GetUserPrivacySettingsRequest>().Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task PrivacySettings_WithNothingCachedAndNoBus_FailClosedToFriends()
    {
        // The headline rule: never Everyone on error.
        var bus = new FakeMessageBus(_ => throw new InvalidOperationException("identity is down"));

        var settings = await PrivacyOver(bus).GetAsync("user-1");

        Assert.Multiple(() =>
        {
            Assert.That(settings.DirectMessagePolicy, Is.EqualTo(DirectMessagePolicy.Friends));
            Assert.That(settings.SendReadReceipts, Is.False);
            Assert.That(settings.SendTypingIndicators, Is.False);
            Assert.That(settings.HidePushContent, Is.True);
            Assert.That(settings.ExplicitContentFilter, Is.EqualTo(ExplicitContentFilter.Everyone));
        });
    }

    [Test]
    public async Task PrivacySettings_RetentionIsNeverInvented()
    {
        // Every other default is the restrictive one. Retention's is not, because enforcing an
        // invented window deletes history nobody asked to lose.
        var bus = new FakeMessageBus(_ => throw new InvalidOperationException("identity is down"));

        var settings = await PrivacyOver(bus).GetAsync("user-1");

        Assert.That(settings.DmRetentionDays, Is.Null);
    }

    [Test]
    public async Task PrivacySettings_AnIdMissingFromTheAnswerGetsRestrictiveDefaults()
    {
        // Identity is specified to answer for every id; if it ever does not, the gap must not read
        // as "no restriction".
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetUserPrivacySettingsRequest => new GetUserPrivacySettingsResponse
            {
                Settings = [Settings("user-1", DirectMessagePolicy.Everyone)],
            },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var result = await PrivacyOver(bus).GetAsync(["user-1", "user-2"]);

        Assert.Multiple(() =>
        {
            Assert.That(result["user-1"].DirectMessagePolicy, Is.EqualTo(DirectMessagePolicy.Everyone));
            Assert.That(result["user-2"].DirectMessagePolicy, Is.EqualTo(DirectMessagePolicy.Friends));
        });
    }

    [Test]
    public async Task PrivacySettingsChangedEvent_EvictsTheEntry()
    {
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetUserPrivacySettingsRequest => new GetUserPrivacySettingsResponse
            {
                Settings = [Settings("user-1", DirectMessagePolicy.Everyone)],
            },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var cache = PrivacyOver(bus);
        await cache.GetAsync("user-1");
        Assert.That(_cache.HasEntry(PrivacySettingsCache.KeyFor("user-1")), Is.True);

        await PrivacyCacheInvalidationHandlers.Handle(
            new UserPrivacySettingsChangedEvent { UserId = "user-1", Version = 4 }, cache);

        Assert.That(_cache.HasEntry(PrivacySettingsCache.KeyFor("user-1")), Is.False);
    }

    // ── BlockCache ────────────────────────────────────────────────────────────

    [Test]
    public async Task Blocks_AreSplitByDirection()
    {
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetBlockRelationshipsRequest => new GetBlockRelationshipsResponse
            {
                Blocks =
                [
                    new BlockRelationship { BlockerId = "user-1", BlockedId = "pest" },
                    new BlockRelationship { BlockerId = "enemy", BlockedId = "user-1" },
                ],
            },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var relations = await BlocksOver(bus).GetAsync("user-1");

        Assert.Multiple(() =>
        {
            Assert.That(relations.Blocking, Is.EquivalentTo(new[] { "pest" }));
            Assert.That(relations.BlockedBy, Is.EquivalentTo(new[] { "enemy" }));
            Assert.That(relations.Degraded, Is.False);
            Assert.That(BlockCache.KeyFor("usr_abc"), Is.EqualTo("blocks:user_id:usr_abc"));
        });
    }

    [Test]
    public async Task Blocks_WithNothingCachedAndNoBus_AreDegraded()
    {
        var bus = new FakeMessageBus(_ => throw new InvalidOperationException("social is down"));

        var relations = await BlocksOver(bus).GetAsync("user-1");

        Assert.That(relations.Degraded, Is.True,
            "An authorization caller must be able to tell 'no blocks' from 'we could not find out'");
    }

    [Test]
    public async Task Blocks_DegradedTreatsEverybodyAsBlockedForFanOut()
    {
        // Suppressing a notification that should have gone out is recoverable. Delivering one the
        // recipient blocked is not.
        var bus = new FakeMessageBus(_ => throw new InvalidOperationException("social is down"));

        var blocked = await BlocksOver(bus).BlockedEitherWayAsync("author", ["a", "b"]);

        Assert.That(blocked, Is.EquivalentTo(new[] { "a", "b" }));
    }

    [Test]
    public async Task Blocks_AStaleCachedAnswerBeatsInventingOne()
    {
        // The fallback order is fresh, then stale, then "unknown". A momentary Social blip must not
        // read as "everything is blocked" while a perfectly good previous answer is sitting there.
        var responding = new FakeMessageBus(msg => msg switch
        {
            GetBlockRelationshipsRequest => new GetBlockRelationshipsResponse
            {
                Blocks = [new BlockRelationship { BlockerId = "user-1", BlockedId = "pest" }],
            },
            _ => throw new InvalidOperationException("unexpected"),
        });

        await BlocksOver(responding).GetAsync("user-1");

        var broken = new FakeMessageBus(_ => throw new InvalidOperationException("social is down"));
        var relations = await BlocksOver(broken).GetAsync("user-1");

        Assert.Multiple(() =>
        {
            Assert.That(relations.Degraded, Is.False);
            Assert.That(relations.Blocking, Is.EquivalentTo(new[] { "pest" }));
        });
    }

    [Test]
    public async Task BlockEvents_EvictBothSides()
    {
        // Keyed per user and holding both directions, so evicting only the blocker leaves the
        // blocked party's record - the one that decides whether *they* may reach back - stale.
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetBlockRelationshipsRequest => new GetBlockRelationshipsResponse { Blocks = [] },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var cache = BlocksOver(bus);
        await cache.GetAsync("user-1");
        await cache.GetAsync("user-2");

        await PrivacyCacheInvalidationHandlers.Handle(
            new UserBlockedEvent { BlockerId = "user-1", BlockedId = "user-2" }, cache);

        Assert.Multiple(() =>
        {
            Assert.That(_cache.HasEntry(BlockCache.KeyFor("user-1")), Is.False);
            Assert.That(_cache.HasEntry(BlockCache.KeyFor("user-2")), Is.False);
        });
    }

    [Test]
    public async Task UnblockEvent_EvictsBothSidesToo()
    {
        var bus = new FakeMessageBus(msg => msg switch
        {
            GetBlockRelationshipsRequest => new GetBlockRelationshipsResponse { Blocks = [] },
            _ => throw new InvalidOperationException("unexpected"),
        });

        var cache = BlocksOver(bus);
        await cache.GetAsync("user-1");
        await cache.GetAsync("user-2");

        await PrivacyCacheInvalidationHandlers.Handle(
            new UserUnblockedEvent { BlockerId = "user-1", BlockedId = "user-2" }, cache);

        Assert.Multiple(() =>
        {
            Assert.That(_cache.HasEntry(BlockCache.KeyFor("user-1")), Is.False);
            Assert.That(_cache.HasEntry(BlockCache.KeyFor("user-2")), Is.False);
        });
    }
}
