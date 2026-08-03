using Identity.Application.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Tests.Services;

/// <summary>Mirrors VerificationCodeServiceTests - PasswordResetCodeService explicitly follows the
/// same "reuse an existing still-valid code" pattern to avoid the overwrite race this codebase hit
/// previously with e-mail verification codes (see project memory / VerificationCodeServiceTests).</summary>
[TestFixture]
public class PasswordResetCodeServiceTests
{
    private IDistributedCache _cache = null!;

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        _cache = services.BuildServiceProvider().GetRequiredService<IDistributedCache>();
    }

    [Test]
    public async Task GetOrCreateCodeAsync_CalledTwiceForSameEmail_ReturnsSameCode()
    {
        const string email = "reset@example.com";

        var first = await PasswordResetCodeService.GetOrCreateCodeAsync(_cache, email);
        var second = await PasswordResetCodeService.GetOrCreateCodeAsync(_cache, email);

        Assert.That(second, Is.EqualTo(first),
            "A repeat password-reset request must not invalidate a code already e-mailed to the user.");
    }

    [Test]
    public async Task GetOrCreateCodeAsync_DifferentEmails_ReturnsDifferentCodes()
    {
        var codeA = await PasswordResetCodeService.GetOrCreateCodeAsync(_cache, "a@example.com");
        var codeB = await PasswordResetCodeService.GetOrCreateCodeAsync(_cache, "b@example.com");

        // Each is keyed independently; collision is not the property under test.
        Assert.That(_cache.GetString("password_reset_code:a@example.com"), Is.EqualTo(codeA));
        Assert.That(_cache.GetString("password_reset_code:b@example.com"), Is.EqualTo(codeB));
    }

    [Test]
    public async Task GetOrCreateCodeAsync_StoresUnderExpectedCacheKey()
    {
        const string email = "keyed@example.com";

        var code = await PasswordResetCodeService.GetOrCreateCodeAsync(_cache, email);

        Assert.That(_cache.GetString($"password_reset_code:{email}"), Is.EqualTo(code));
    }

    [Test]
    public async Task RemoveAsync_ClearsTheCachedCode()
    {
        const string email = "toremove@example.com";
        await PasswordResetCodeService.GetOrCreateCodeAsync(_cache, email);

        await PasswordResetCodeService.RemoveAsync(_cache, email);

        Assert.That(_cache.GetString($"password_reset_code:{email}"), Is.Null);
    }

    [Test]
    public async Task GetOrCreateCodeAsync_AfterRemoval_MintsANewCode()
    {
        const string email = "reissue@example.com";
        var first = await PasswordResetCodeService.GetOrCreateCodeAsync(_cache, email);
        await PasswordResetCodeService.RemoveAsync(_cache, email);

        var second = await PasswordResetCodeService.GetOrCreateCodeAsync(_cache, email);

        // Not asserting inequality (a fresh code could theoretically collide), just that a new
        // entry exists after removal rather than the call short-circuiting on nothing.
        Assert.That(second, Is.Not.Null.And.Not.Empty);
        Assert.That(_cache.GetString($"password_reset_code:{email}"), Is.EqualTo(second));
    }

    // ── Entropy and failed-attempt lifecycle ─────────────────────────────────
    // The code used to be the first 6 hex chars of a GUID (2^24) and a wrong guess left it intact
    // for its full 15-minute life, so a single live code could be guessed without limit. Both
    // properties are now owned by OneTimeCodeService.

    [Test]
    public async Task GetOrCreateCodeAsync_MintsAtLeast48BitsOfEntropy()
    {
        var code = await PasswordResetCodeService.GetOrCreateCodeAsync(_cache, "entropy@example.com");

        Assert.That(code, Has.Length.GreaterThanOrEqualTo(12),
            "A password-reset code is the sole credential in front of an account takeover; 6 hex chars is 2^24.");
        Assert.That(code, Does.Match("^[0-9a-f]+$"));
    }

    [Test]
    public async Task ValidateAsync_CorrectCode_SucceedsAndConsumesTheCode()
    {
        const string email = "consume@example.com";
        var code = await PasswordResetCodeService.GetOrCreateCodeAsync(_cache, email);

        var first = await PasswordResetCodeService.ValidateAsync(_cache, email, code);
        var replay = await PasswordResetCodeService.ValidateAsync(_cache, email, code);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(OneTimeCodeResult.Valid));
            Assert.That(replay, Is.EqualTo(OneTimeCodeResult.Expired), "a consumed code must not be replayable");
        });
    }

    [Test]
    public async Task ValidateAsync_RepeatedWrongGuesses_DestroyTheCode()
    {
        const string email = "bruteforce@example.com";
        var code = await PasswordResetCodeService.GetOrCreateCodeAsync(_cache, email);

        OneTimeCodeResult last = OneTimeCodeResult.Valid;
        for (var i = 0; i < OneTimeCodeService.MaxAttempts; i++)
        {
            last = await PasswordResetCodeService.ValidateAsync(_cache, email, "000000000000");
        }

        Assert.Multiple(() =>
        {
            Assert.That(last, Is.EqualTo(OneTimeCodeResult.TooManyAttempts));
            Assert.That(_cache.GetString($"password_reset_code:{email}"), Is.Null,
                "the code must be destroyed after too many wrong guesses, not left live for the rest of its TTL");
        });

        // Even the real code is now useless - the attacker's guessing burned it.
        Assert.That(await PasswordResetCodeService.ValidateAsync(_cache, email, code),
            Is.EqualTo(OneTimeCodeResult.Expired));
    }

    [Test]
    public async Task GetOrCreateCodeAsync_AfterAFreshCode_ResetsTheAttemptCounter()
    {
        const string email = "counterreset@example.com";
        await PasswordResetCodeService.GetOrCreateCodeAsync(_cache, email);
        await PasswordResetCodeService.ValidateAsync(_cache, email, "deadbeefcafe");

        await PasswordResetCodeService.RemoveAsync(_cache, email);
        var fresh = await PasswordResetCodeService.GetOrCreateCodeAsync(_cache, email);

        // A new code starts with a full attempt budget rather than inheriting the old one's.
        for (var i = 0; i < OneTimeCodeService.MaxAttempts - 1; i++)
        {
            Assert.That(await PasswordResetCodeService.ValidateAsync(_cache, email, "000000000000"),
                Is.EqualTo(OneTimeCodeResult.Invalid));
        }

        // The budget was not inherited: one attempt short of the limit, the correct code still works.
        Assert.That(await PasswordResetCodeService.ValidateAsync(_cache, email, fresh),
            Is.EqualTo(OneTimeCodeResult.Valid));
    }
}
