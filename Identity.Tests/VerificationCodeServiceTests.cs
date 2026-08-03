using Identity.Application.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Tests;

/// <summary>
/// Covers the two things a verification code has to get right: its shape (six numeric digits, the
/// format the clients and the e-mail template are written around) and its lifecycle.
/// </summary>
[TestFixture]
public class VerificationCodeServiceTests
{
    private IDistributedCache _cache = null!;

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        _cache = services.BuildServiceProvider().GetRequiredService<IDistributedCache>();
    }

    /// <summary>
    /// The code is retyped by hand into a fixed-length numeric field, so its shape is part of its
    /// contract, not an implementation detail: exactly six characters, every one a decimal digit.
    /// </summary>
    [Test]
    public async Task GetOrCreateCodeAsync_ReturnsSixNumericDigits()
    {
        for (var i = 0; i < 500; i++)
        {
            var code = await VerificationCodeService.GetOrCreateCodeAsync(_cache, $"shape{i}@example.com");

            Assert.That(code, Has.Length.EqualTo(6), $"'{code}' is not six characters long.");
            Assert.That(code, Does.Match("^[0-9]{6}$"), $"'{code}' contains a non-digit character.");
        }
    }

    /// <summary>Leading zeros survive: "000123" is a legal code and must not be emitted as "123".</summary>
    [Test]
    public async Task GetOrCreateCodeAsync_PadsCodesBelowSixDigits()
    {
        var codes = new List<string>();
        for (var i = 0; i < 2000; i++)
        {
            codes.Add(await VerificationCodeService.GetOrCreateCodeAsync(_cache, $"pad{i}@example.com"));
        }

        // ~10% of draws start with a zero, so a run of this size seeing none means they are being
        // trimmed somewhere.
        Assert.That(codes, Has.Some.StartWith("0"),
            "No code in 2000 draws began with '0' - leading zeros are being stripped, or the range is wrong.");
        Assert.That(codes, Has.All.Length.EqualTo(6));
    }

    [Test]
    public async Task GetOrCreateCodeAsync_DoesNotReturnTheSameCodeForEveryUser()
    {
        var codes = new HashSet<string>();
        for (var i = 0; i < 100; i++)
        {
            codes.Add(await VerificationCodeService.GetOrCreateCodeAsync(_cache, $"unique{i}@example.com"));
        }

        // Collisions across 100 draws from 10^6 are possible but rare; anything near-constant is a
        // broken generator.
        Assert.That(codes, Has.Count.GreaterThan(95));
    }

    [Test]
    public async Task ValidateAsync_AcceptsTheIssuedNumericCode()
    {
        const string email = "roundtrip@example.com";
        var code = await VerificationCodeService.GetOrCreateCodeAsync(_cache, email);

        var result = await VerificationCodeService.ValidateAsync(_cache, email, code);

        Assert.That(result, Is.EqualTo(OneTimeCodeResult.Valid));
    }

    /// <summary>
    /// Shortening the alphabet is affordable for e-mail verification only because a wrong guess
    /// still costs an attempt - 10^6 with unlimited guesses is not a credential. Guards the pairing.
    /// </summary>
    [Test]
    public async Task ValidateAsync_DestroysTheCodeAfterTooManyWrongGuesses()
    {
        const string email = "bruteforce@example.com";
        var code = await VerificationCodeService.GetOrCreateCodeAsync(_cache, email);
        var wrongCode = code == "000000" ? "111111" : "000000";

        for (var i = 0; i < OneTimeCodeService.MaxAttempts - 1; i++)
        {
            Assert.That(await VerificationCodeService.ValidateAsync(_cache, email, wrongCode),
                Is.EqualTo(OneTimeCodeResult.Invalid));
        }

        Assert.That(await VerificationCodeService.ValidateAsync(_cache, email, wrongCode),
            Is.EqualTo(OneTimeCodeResult.TooManyAttempts));

        // The real code is gone too - the user must request a new one.
        Assert.That(await VerificationCodeService.ValidateAsync(_cache, email, code),
            Is.EqualTo(OneTimeCodeResult.Expired));
    }

    /// <summary>The numeric format is scoped to e-mail verification.</summary>
    [Test]
    public async Task PasswordResetCodes_AreNotShortenedToSixDigits()
    {
        var code = await PasswordResetCodeService.GetOrCreateCodeAsync(_cache, "reset@example.com");

        Assert.That(code, Does.Match("^[0-9a-f]{12}$"), $"'{code}' is not a 12-character hex code.");
    }

    [Test]
    public async Task GetOrCreateCodeAsync_CalledTwiceForSameEmail_DoesNotInvalidateThePreviouslyIssuedCode()
    {
        const string email = "racer@example.com";

        // First trigger: e.g. the automatic welcome email sent right after signup.
        var firstCode = await VerificationCodeService.GetOrCreateCodeAsync(_cache, email);

        // Second trigger for the same user before the first code expired: e.g. the
        // user clicks "resend code", or a duplicate delivery of the UserCreated
        // event is handled by the other pod.
        var secondCode = await VerificationCodeService.GetOrCreateCodeAsync(_cache, email);

        Assert.That(secondCode, Is.EqualTo(firstCode),
            "A repeat verification-code request must not invalidate a code that was already e-mailed to the user.");
    }
}
