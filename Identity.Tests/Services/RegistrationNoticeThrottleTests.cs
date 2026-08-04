using Identity.Application.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Identity.Tests.Services;

/// <summary>
/// The cap on "someone tried to sign up with your address" mail.
///
/// <para>Closing the registration enumeration oracle meant an anonymous, unauthenticated endpoint
/// now sends mail to an address the caller does not control. Without a cap that is a mail cannon
/// pointed at any address anyone cares to type, which is a worse problem than the leak it replaced -
/// so the throttle is part of the fix, not a nicety around it.</para>
/// </summary>
[TestFixture]
public class RegistrationNoticeThrottleTests
{
    private IDistributedCache _cache = null!;

    [SetUp]
    public void SetUp() =>
        _cache = new ServiceCollection().AddDistributedMemoryCache()
            .BuildServiceProvider().GetRequiredService<IDistributedCache>();

    // ── normal ──────────────────────────────────────────────────────────────

    [Test]
    public async Task TheFirstNoticeForAnAddressIsAllowed()
    {
        Assert.That(await RegistrationNoticeThrottle.TryAcquireAsync(_cache, "someone@example.com"), Is.True);
    }

    [Test]
    public async Task AnAddressGetsExactlyItsBudgetAndThenNothing()
    {
        const string email = "victim@example.com";

        for (var i = 0; i < RegistrationNoticeThrottle.MaxPerWindow; i++)
        {
            Assert.That(await RegistrationNoticeThrottle.TryAcquireAsync(_cache, email), Is.True,
                $"notice {i + 1} is inside the budget");
        }

        var overCap = await RegistrationNoticeThrottle.TryAcquireAsync(_cache, email);
        var stillOverCap = await RegistrationNoticeThrottle.TryAcquireAsync(_cache, email);

        Assert.Multiple(() =>
        {
            Assert.That(overCap, Is.False);
            Assert.That(stillOverCap, Is.False,
                "and it stays refused - a flood must not walk the counter back under the cap");
        });
    }

    // ── edge ────────────────────────────────────────────────────────────────

    [Test]
    public async Task TheBudgetIsPerAddress()
    {
        var exhausted = "exhausted@example.com";
        for (var i = 0; i < RegistrationNoticeThrottle.MaxPerWindow; i++)
            await RegistrationNoticeThrottle.TryAcquireAsync(_cache, exhausted);

        var hammered = await RegistrationNoticeThrottle.TryAcquireAsync(_cache, exhausted);
        var bystander = await RegistrationNoticeThrottle.TryAcquireAsync(_cache, "bystander@example.com");

        Assert.Multiple(() =>
        {
            Assert.That(hammered, Is.False);
            Assert.That(bystander, Is.True,
                "one address being hammered must not silence everyone else's notices");
        });
    }

    [Test]
    public async Task TheCounterDoesNotCollideWithACodeForTheSameAddress()
    {
        const string email = "shared@example.com";

        await VerificationCodeService.GetOrCreateCodeAsync(_cache, email);
        var code = await _cache.GetStringAsync($"verification_code:{email}");

        await RegistrationNoticeThrottle.TryAcquireAsync(_cache, email);

        Assert.That(await _cache.GetStringAsync($"verification_code:{email}"), Is.EqualTo(code),
            "the throttle key must not overwrite a live verification code - the last time two "
            + "features shared a cache key, valid codes were being invalidated under users");
    }

    // ── the dispatcher actually consults it ─────────────────────────────────

    /// <summary>
    /// A cap nothing calls is not a cap. Observed through the verification-code branch, which is the
    /// one this can see from outside: while the address is under its budget the dispatcher mints a
    /// code, and once the budget is gone it does nothing at all.
    /// </summary>
    [Test]
    public async Task TheDispatcherStopsSendingOnceTheBudgetIsGone()
    {
        const string email = "flooded@example.com";
        var dispatcher = new AccountEmailDispatcher(null!, NullLogger<AccountEmailDispatcher>.Instance);

        for (var i = 0; i < RegistrationNoticeThrottle.MaxPerWindow; i++)
        {
            await dispatcher.QueueRegistrationAttemptNoticeAsync(
                _cache, email, "flooded_user", accountAwaitsVerification: true);
        }

        Assert.That(await _cache.GetStringAsync($"verification_code:{email}"), Is.Not.Null,
            "inside the budget the dispatcher does its work");

        // Clear the evidence of the allowed sends, then go one over the cap.
        await VerificationCodeService.RemoveAsync(_cache, email);

        await dispatcher.QueueRegistrationAttemptNoticeAsync(
            _cache, email, "flooded_user", accountAwaitsVerification: true);

        Assert.That(await _cache.GetStringAsync($"verification_code:{email}"), Is.Null,
            "over the cap the dispatcher must not even reach the mint - the send is suppressed "
            + "entirely, and the HTTP response says the same thing either way");
    }

    /// <summary>The repeated sends inside the budget must not each mint a fresh code: overwriting a
    /// live code is the regression that broke email verification once already
    /// (<see cref="OneTimeCodeService.GetOrCreateCodeAsync"/> is idempotent by design).</summary>
    [Test]
    public async Task RepeatedNoticesReuseTheOutstandingVerificationCode()
    {
        const string email = "repeat@example.com";
        var dispatcher = new AccountEmailDispatcher(null!, NullLogger<AccountEmailDispatcher>.Instance);

        await dispatcher.QueueRegistrationAttemptNoticeAsync(
            _cache, email, "repeat_user", accountAwaitsVerification: true);
        var first = await _cache.GetStringAsync($"verification_code:{email}");

        await dispatcher.QueueRegistrationAttemptNoticeAsync(
            _cache, email, "repeat_user", accountAwaitsVerification: true);

        Assert.That(await _cache.GetStringAsync($"verification_code:{email}"), Is.EqualTo(first));
    }
}
