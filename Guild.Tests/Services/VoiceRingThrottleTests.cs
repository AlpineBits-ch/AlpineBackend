using Guild.Application.Services;
using Guild.Tests.Helpers;

namespace Guild.Tests.Services;

/// <summary>
/// The three limits that stop the ring endpoint from being a harassment tool, each pinned against
/// the abuse it exists for.
/// </summary>
[TestFixture]
public class VoiceRingThrottleTests
{
    private const string Inviter = "user-inviter";
    private const string Target = "user-target";

    private FakeDistributedCache _cache = null!;
    private TestClock _clock = null!;
    private VoiceRingThrottle _throttle = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _clock = new TestClock(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero));
        _throttle = new VoiceRingThrottle(_cache) { Clock = _clock };
    }

    private Task<VoiceRingThrottleVerdict> TryAsync(string inviter = Inviter, string target = Target) =>
        _throttle.TryAcquireAsync(inviter, target);

    [Test]
    public async Task AFirstRingIsAllowed()
    {
        var verdict = await TryAsync();

        Assert.Multiple(() =>
        {
            Assert.That(verdict.Allowed, Is.True);
            Assert.That(verdict.Reason, Is.Null);
        });
    }

    [Test]
    public async Task OneAccountRingingManyPeopleIsCutOffAtTheInviterCap()
    {
        for (var i = 0; i < VoiceRingThrottle.MaxPerInviter; i++)
        {
            var allowed = await TryAsync(target: $"user-target-{i}");
            Assert.That(allowed.Allowed, Is.True, $"ring {i} should still be within budget");
        }

        var refused = await TryAsync(target: "user-target-last");

        Assert.Multiple(() =>
        {
            Assert.That(refused.Allowed, Is.False);
            Assert.That(refused.Reason, Is.EqualTo(VoiceRingRefusal.InviterFlooding));
            Assert.That(refused.RetryAfter, Is.EqualTo(VoiceRingThrottle.Window));
        });
    }

    [Test]
    public async Task ManyAccountsRingingOnePersonIsCutOffAtTheTargetCap()
    {
        // Each inviter is individually well inside their own budget - the pile-on is only visible
        // from the target's side, which is exactly why the second counter exists.
        for (var i = 0; i < VoiceRingThrottle.MaxPerTarget; i++)
        {
            var allowed = await TryAsync($"user-inviter-{i}");
            Assert.That(allowed.Allowed, Is.True);
        }

        var refused = await TryAsync("user-inviter-last");

        Assert.Multiple(() =>
        {
            Assert.That(refused.Allowed, Is.False);
            Assert.That(refused.Reason, Is.EqualTo(VoiceRingRefusal.TargetSaturated));
        });
    }

    [Test]
    public async Task ADeclineShutsThatOneInviterOutForTheFirstCooldown()
    {
        await _throttle.RecordDeclineAsync(Inviter, Target);

        var refused = await TryAsync();

        Assert.Multiple(() =>
        {
            Assert.That(refused.Allowed, Is.False);
            Assert.That(refused.Reason, Is.EqualTo(VoiceRingRefusal.RecentlyDeclined));
            Assert.That(refused.RetryAfter, Is.EqualTo(VoiceRingThrottle.DeclineCooldowns[0]));
        });
    }

    [Test]
    public async Task ADeclineDoesNotShutAnybodyElseOut()
    {
        await _throttle.RecordDeclineAsync(Inviter, Target);

        var other = await TryAsync("user-someone-else");

        Assert.That(other.Allowed, Is.True,
            "one person's refusal says nothing about whether the target wants to hear from anybody else");
    }

    [Test]
    public async Task TheCooldownEndsOnItsOwn()
    {
        await _throttle.RecordDeclineAsync(Inviter, Target);
        _clock.Advance(VoiceRingThrottle.DeclineCooldowns[0] + TimeSpan.FromMinutes(1));

        // The stored instant has passed.
        var verdict = await TryAsync();

        Assert.That(verdict.Allowed, Is.True);
    }

    [Test]
    public async Task RepeatedDeclinesGetProgressivelyLongerCooldowns()
    {
        var first = await _throttle.RecordDeclineAsync(Inviter, Target);
        var second = await _throttle.RecordDeclineAsync(Inviter, Target);
        var third = await _throttle.RecordDeclineAsync(Inviter, Target);
        var fourth = await _throttle.RecordDeclineAsync(Inviter, Target);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(VoiceRingThrottle.DeclineCooldowns[0]));
            Assert.That(second, Is.EqualTo(VoiceRingThrottle.DeclineCooldowns[1]));
            Assert.That(third, Is.EqualTo(VoiceRingThrottle.DeclineCooldowns[2]));
            Assert.That(fourth, Is.EqualTo(VoiceRingThrottle.DeclineCooldowns[^1]),
                "the last rung repeats rather than growing without bound - this is not a permanent block");
        });
    }

    [Test]
    public async Task WaitingOutALockoutLandsOnTheNextRungRatherThanTheFirst()
    {
        await _throttle.RecordDeclineAsync(Inviter, Target);
        _clock.Advance(VoiceRingThrottle.DeclineCooldowns[0] + TimeSpan.FromMinutes(1));

        var next = await _throttle.RecordDeclineAsync(Inviter, Target);

        Assert.That(next, Is.EqualTo(VoiceRingThrottle.DeclineCooldowns[1]),
            "the pair's history outlives the lockout, or persistence would cost nothing");
    }

    [Test]
    public async Task ThePairCooldownIsCheckedBeforeTheVolumeBudgets()
    {
        await _throttle.RecordDeclineAsync(Inviter, Target);

        await TryAsync();
        await TryAsync();
        await TryAsync();

        // None of those were charged, so a ring to somebody else still goes through.
        var elsewhere = await TryAsync(target: "user-other");

        Assert.That(elsewhere.Allowed, Is.True,
            "a locked-out inviter must not be able to burn their own quota against a wall");
    }

    [Test]
    public async Task ARefundLetsTheNextRingThrough()
    {
        for (var i = 0; i < VoiceRingThrottle.MaxPerInviter; i++) await TryAsync(target: $"user-{i}");
        await _throttle.RefundAsync(Inviter, "user-0");

        var verdict = await TryAsync(target: "user-new");

        Assert.That(verdict.Allowed, Is.True);
    }

    [Test]
    public async Task ARefundCannotDriveACounterBelowZero()
    {
        await _throttle.RefundAsync(Inviter, Target);
        await _throttle.RefundAsync(Inviter, Target);

        for (var i = 0; i < VoiceRingThrottle.MaxPerInviter; i++)
        {
            var allowed = await TryAsync(target: $"user-{i}");
            Assert.That(allowed.Allowed, Is.True);
        }

        var refused = await TryAsync(target: "user-last");
        Assert.That(refused.Allowed, Is.False,
            "refunding a ring that was never charged must not mint budget");
    }
}
