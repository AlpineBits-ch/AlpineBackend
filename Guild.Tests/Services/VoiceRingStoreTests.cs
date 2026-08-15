using Guild.Application.Models;
using Guild.Application.Services;
using Guild.Contracts;
using Guild.Tests.Helpers;

namespace Guild.Tests.Services;

/// <summary>
/// The ephemeral ring state itself: one authoritative record, two indexes that only ever hint at it,
/// and exactly one transition out of pending however many callers race for it.
/// </summary>
[TestFixture]
public class VoiceRingStoreTests
{
    private FakeDistributedCache _cache = null!;
    private TestClock _clock = null!;
    private VoiceRingStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _clock = new TestClock(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero));
        _store = new VoiceRingStore(new FakeDistributedLockService(), _cache) { Clock = _clock };
    }

    private VoiceRing NewRing(
        string inviter = "user-a", string target = "user-b", string channelId = "channel-1") => new()
    {
        Id = VoiceRing.GenerateId(),
        GuildId = "guild-1",
        ChannelId = channelId,
        InviterId = inviter,
        TargetUserId = target,
        CreatedAt = _clock.UtcNowValue.UtcDateTime,
        ExpiresAt = _clock.UtcNowValue.UtcDateTime.Add(VoiceRing.Ttl),
    };

    [Test]
    public async Task ARingIsFindableFromBothEnds()
    {
        var ring = NewRing();
        await _store.CreateAsync(ring);

        var byTarget = await _store.PendingForTargetAsync("user-b");
        var byChannel = await _store.PendingForChannelAsync("channel-1");

        Assert.Multiple(() =>
        {
            Assert.That(byTarget.Select(r => r.Id), Is.EqualTo(new[] { ring.Id }));
            Assert.That(byChannel.Select(r => r.Id), Is.EqualTo(new[] { ring.Id }),
                "the channel index answers questions the target index cannot - who to cancel when an inviter leaves");
        });
    }

    [Test]
    public async Task TwoPeopleCanBeAskingTheSameTargetAtOnce()
    {
        var first = NewRing("user-a");
        var second = NewRing("user-c", channelId: "channel-2");
        await _store.CreateAsync(first);
        await _store.CreateAsync(second);

        var pending = await _store.PendingForTargetAsync("user-b");

        Assert.That(pending.Select(r => r.InviterId), Is.EquivalentTo(new[] { "user-a", "user-c" }),
            "a single-id index would silently drop one of them");
    }

    [Test]
    public async Task OnlyOneCallerEverMakesTheTransition()
    {
        var ring = NewRing();
        await _store.CreateAsync(ring);

        var accepted = await _store.ResolveAsync(ring.Id, VoiceRingStatus.Accepted, null, "phone");
        var declined = await _store.ResolveAsync(ring.Id, VoiceRingStatus.Declined, null, "laptop");

        Assert.Multiple(() =>
        {
            Assert.That(accepted.Transitioned, Is.True);
            Assert.That(declined.Transitioned, Is.False);
            Assert.That(declined.AlreadyResolved, Is.True);
            Assert.That(declined.Ring!.Status, Is.EqualTo(VoiceRingStatus.Accepted));
            Assert.That(declined.Ring.ResolvedByDeviceId, Is.EqualTo("phone"),
                "the second caller must be able to see which device won, not just that it lost");
        });
    }

    [Test]
    public async Task ResolvingARingThatNeverExistedIsNotFound()
    {
        var missing = await _store.ResolveAsync("ring_nothing", VoiceRingStatus.Declined, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(missing.NotFound, Is.True);
            Assert.That(missing.Transitioned, Is.False);
        });
    }

    [Test]
    public async Task ARingPastItsDeadlineRefusesAnAcceptAndRecordsWhy()
    {
        var ring = NewRing();
        await _store.CreateAsync(ring);
        _clock.Advance(VoiceRing.Ttl + TimeSpan.FromSeconds(1));

        var accept = await _store.ResolveAsync(ring.Id, VoiceRingStatus.Accepted, null, "phone");

        Assert.Multiple(() =>
        {
            Assert.That(accept.Transitioned, Is.False,
                "the deadline decides, not the scheduled expiry message, which can be late");
            Assert.That(accept.Ring!.Status, Is.EqualTo(VoiceRingStatus.Expired));
            Assert.That(accept.Ring.Reason, Is.EqualTo(VoiceRingReason.TimedOut));
        });
    }

    [Test]
    public async Task TheExpiryItselfIsAllowedToClaimAnExpiryItDidNotCause()
    {
        var ring = NewRing();
        await _store.CreateAsync(ring);
        _clock.Advance(VoiceRing.Ttl + TimeSpan.FromSeconds(1));

        var expiry = await _store.ResolveAsync(ring.Id, VoiceRingStatus.Expired, VoiceRingReason.TimedOut, null);

        Assert.That(expiry.Transitioned, Is.True,
            "otherwise nothing ever fans out the notification that the invitation lapsed");
    }

    [Test]
    public async Task ALapsedRingDisappearsFromBothIndexesOnTheNextRead()
    {
        var ring = NewRing();
        await _store.CreateAsync(ring);
        _clock.Advance(VoiceRing.Ttl + TimeSpan.FromSeconds(1));

        var byTarget = await _store.PendingForTargetAsync("user-b");
        var byChannel = await _store.PendingForChannelAsync("channel-1");

        Assert.Multiple(() =>
        {
            Assert.That(byTarget, Is.Empty);
            Assert.That(byChannel, Is.Empty);
        });
    }

    [Test]
    public async Task AResolvedRingIsStillReadableById()
    {
        var ring = NewRing();
        await _store.CreateAsync(ring);
        await _store.ResolveAsync(ring.Id, VoiceRingStatus.Declined, null, "phone");

        var stored = await _store.LoadAsync(ring.Id);

        Assert.That(stored!.Status, Is.EqualTo(VoiceRingStatus.Declined),
            "a second handset asking what happened needs 'declined', not 'no such ring'");
    }

    [Test]
    public async Task PruningOneRingLeavesTheOthersIndexed()
    {
        var lapsing = NewRing(channelId: "channel-1");
        await _store.CreateAsync(lapsing);

        _clock.Advance(VoiceRing.Ttl - TimeSpan.FromSeconds(5));
        var fresh = NewRing(inviter: "user-c", channelId: "channel-1");
        fresh.CreatedAt = _clock.UtcNowValue.UtcDateTime;
        fresh.ExpiresAt = _clock.UtcNowValue.UtcDateTime.Add(VoiceRing.Ttl);
        await _store.CreateAsync(fresh);

        _clock.Advance(TimeSpan.FromSeconds(10));

        var pending = await _store.PendingForChannelAsync("channel-1");
        Assert.That(pending.Select(r => r.Id), Is.EqualTo(new[] { fresh.Id }));

        // And the surviving one is still there on a second read, which is what a blind overwrite of
        // the index would have destroyed.
        Assert.That((await _store.PendingForChannelAsync("channel-1")).Select(r => r.Id),
            Is.EqualTo(new[] { fresh.Id }));
    }
}
