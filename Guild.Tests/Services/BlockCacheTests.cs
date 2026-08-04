using Guild.Application.Services;
using Guild.Tests.Helpers;
using Social.Contracts.Bus.Integration.Request;

namespace Guild.Tests.Services;

/// <summary>Covers <see cref="BlockCache"/> and the <see cref="BlockView"/> it hands out.</summary>
[TestFixture]
public class BlockCacheTests
{
    private const string Blocker = "user-blocker";
    private const string Blocked = "user-blocked";
    private const string Bystander = "user-bystander";

    private FakeDistributedCache _cache = null!;
    private FakeInvokingMessageBus _bus = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _bus = new FakeInvokingMessageBus();
    }

    // ── Normal ────────────────────────────────────────────────────────────────

    [Test]
    public async Task AreBlocked_ForABlockedPair_IsTrueFromEitherSide()
    {
        var sut = PrivacyTestFactory.Blocks(_bus, _cache, (Blocker, Blocked));

        var view = await sut.GetAsync([Blocker, Blocked]);

        Assert.Multiple(() =>
        {
            Assert.That(view.AreBlocked(Blocker, Blocked), Is.True);
            Assert.That(view.AreBlocked(Blocked, Blocker), Is.True);
        });
    }

    [Test]
    public async Task AreBlocked_ForAnUnrelatedPair_IsFalse()
    {
        var sut = PrivacyTestFactory.Blocks(_bus, _cache, (Blocker, Blocked));

        var view = await sut.GetAsync([Blocker]);

        Assert.That(view.AreBlocked(Blocker, Bystander), Is.False);
    }

    [Test]
    public async Task AreBlocked_ResolvingOnlyOneSide_StillDecidesThePair()
    {
        // The whole reason both directions live under one key: a fan-out resolves the author and
        // nobody else, and must still get every recipient right.
        var sut = PrivacyTestFactory.Blocks(_bus, _cache, (Blocked, Blocker));

        var view = await sut.GetAsync([Blocker]);

        Assert.Multiple(() =>
        {
            Assert.That(view.AreBlocked(Blocker, Blocked), Is.True);
            Assert.That(view.AreBlocked(Blocker, Bystander), Is.False);
        });
    }

    [Test]
    public async Task Reachable_RemovesOnlyTheBlockedCounterparties()
    {
        var sut = PrivacyTestFactory.Blocks(_bus, _cache, (Blocker, Blocked));

        var view = await sut.GetAsync([Blocker]);

        Assert.That(view.Reachable(Blocker, [Blocked, Bystander]), Is.EqualTo(new[] { Bystander }));
    }

    [Test]
    public async Task Get_CachesUnderThePrefixedKey_AndServesTheSecondReadFromIt()
    {
        var sut = PrivacyTestFactory.Blocks(_bus, _cache, (Blocker, Blocked));

        await sut.GetAsync([Blocker]);
        await sut.GetAsync([Blocker]);

        Assert.Multiple(() =>
        {
            Assert.That(_cache.HasEntry($"blocks:user_id:{Blocker}"), Is.True);
            Assert.That(_bus.Invoked.OfType<GetBlockRelationshipsRequest>().Count(), Is.EqualTo(1));
        });
    }

    // ── Edge ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task AreBlocked_AUserAgainstThemselves_IsNeverBlocked()
    {
        // Otherwise a user's own presence and typing events would stop reaching their own clients.
        var sut = PrivacyTestFactory.UnreachableBlocks(_bus, _cache);

        var view = await sut.GetAsync([Blocker]);

        Assert.That(view.AreBlocked(Blocker, Blocker), Is.False);
    }

    [Test]
    public async Task Get_NoBlocksAtAll_ResolvesToAnEmptyStateNotAnUnknownOne()
    {
        var sut = PrivacyTestFactory.Blocks(_bus, _cache);

        var view = await sut.GetAsync([Blocker]);

        Assert.That(view.AreBlocked(Blocker, Bystander), Is.False);
    }

    [Test]
    public async Task Invalidate_DropsTheEntry_SoTheNextReadReachesSocialAgain()
    {
        var sut = PrivacyTestFactory.Blocks(_bus, _cache, (Blocker, Blocked));

        await sut.GetAsync([Blocker]);
        await sut.InvalidateAsync(Blocker);
        await sut.GetAsync([Blocker]);

        Assert.That(_bus.Invoked.OfType<GetBlockRelationshipsRequest>().Count(), Is.EqualTo(2));
    }

    // ── Negative ──────────────────────────────────────────────────────────────

    [Test]
    public async Task AreBlocked_WhenSocialIsUnreachable_TreatsThePairAsBlocked()
    {
        var sut = PrivacyTestFactory.UnreachableBlocks(_bus, _cache);

        var view = await sut.GetAsync([Blocker]);

        Assert.That(view.AreBlocked(Blocker, Bystander), Is.True);
    }

    [Test]
    public async Task Get_WhenSocialIsUnreachable_DoesNotCacheTheFailure()
    {
        var sut = PrivacyTestFactory.UnreachableBlocks(_bus, _cache);

        await sut.GetAsync([Blocker]);

        Assert.That(_cache.HasEntry($"blocks:user_id:{Blocker}"), Is.False);
    }

    [Test]
    public void AreBlocked_OnAViewThatKnowsNobody_IsTrue()
    {
        var view = BlockView.Unresolved([Blocker]);

        Assert.That(view.AreBlocked(Blocker, Bystander), Is.True);
    }
}
