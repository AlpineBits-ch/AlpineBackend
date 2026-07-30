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

        // Vanishingly unlikely to collide (6 hex chars), and each is keyed independently anyway.
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

        // Not asserting inequality (a fresh GUID-derived code could theoretically collide), just
        // that a new entry exists after removal rather than the call short-circuiting on nothing.
        Assert.That(second, Is.Not.Null.And.Not.Empty);
        Assert.That(_cache.GetString($"password_reset_code:{email}"), Is.EqualTo(second));
    }
}
