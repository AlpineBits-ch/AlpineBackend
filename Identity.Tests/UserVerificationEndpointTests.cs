using System.Net;
using Alba;
using Identity.Application.Dtos.Request;
using Identity.Application.Services;
using Identity.Infrastructure.Persistence;
using Identity.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Tests;

/// <summary>
/// Reproduces the reported bug: a brand-new user enters the verification code that was
/// just emailed to them and the check fails, even though the code is correct.
///
/// Root cause: both endpoints in UserVerificationEndpoint look the user up with
///   ctx.Users.FirstOrDefault(x => x.NormalizedUserName == email.ToUpperInvariant())
/// i.e. they compare the caller-supplied e-mail address against NormalizedUserName
/// instead of NormalizedEmail. Registration takes Username and Email as two distinct
/// fields, so for any account where the two differ (the normal case) the lookup finds
/// no user, both endpoints silently short-circuit on `user == null` and return
/// Results.Accepted() without ever checking the code - so EmailConfirmed is never set,
/// no matter how many times the correct code is submitted.
/// </summary>
[TestFixture]
public class UserVerificationEndpointTests
{
    private static IAlbaHost Host => AppFixture.Host;

    [Test]
    public async Task VerifyEmail_UsernameDiffersFromEmail_StillMarksEmailConfirmed()
    {
        var email = $"verify-{Guid.NewGuid()}@example.com";
        var username = $"user{Guid.NewGuid():N}"[..12];

        await Host.Scenario(x =>
        {
            x.Post.Json(new CreateUserRequest
            {
                Email = email,
                Password = "SecurePass123!",
                Username = username,
                BirthDate = DateTime.UtcNow.AddYears(-20),
            }).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        const string code = "abc123";

        using (var scope = Host.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
            var cache = scope.ServiceProvider.GetRequiredService<IDistributedCache>();

            var user = await ctx.Users.FirstAsync(u => u.Email == email);
            user.EmailConfirmed = false;
            await ctx.SaveChangesAsync();

            await cache.SetStringAsync($"verification_code:{email}", code);
        }

        await Host.Scenario(x =>
        {
            x.Get.Url($"/api/v1/user/verify-email?email={Uri.EscapeDataString(email)}&code={code}");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var verifyScope = Host.Services.CreateScope();
        var verifyCtx = verifyScope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var verifiedUser = await verifyCtx.Users.FirstAsync(u => u.Email == email);
        Assert.That(verifiedUser.EmailConfirmed, Is.True,
            "verify-email must look the user up by e-mail, not username, so a correct code actually confirms the account.");
    }

    /// <summary>
    /// Reproduces the "first attempt always fails, resend fixes it" report: the
    /// signup welcome email caches its code under the account's real e-mail
    /// address (see UserCreatedHandler), but the post-login "verify your email"
    /// prompt only has the identifier the user typed to log in - which is
    /// commonly their username, not their e-mail. Before both endpoints resolved
    /// the user first and keyed the cache off `user.Email`, looking the code up
    /// by username hit a cache entry that was never written, so the first
    /// verify always 400'd with "Verification code not found" no matter how
    /// correct the code was - only a resend (which then minted and cached a
    /// code under that same username key) let a later attempt succeed.
    /// </summary>
    [Test]
    public async Task VerifyEmail_LookedUpByUsername_FindsCodeCachedUnderRealEmail()
    {
        var email = $"verify-{Guid.NewGuid()}@example.com";
        var username = $"user{Guid.NewGuid():N}"[..12];

        await Host.Scenario(x =>
        {
            x.Post.Json(new CreateUserRequest
            {
                Email = email,
                Password = "SecurePass123!",
                Username = username,
                BirthDate = DateTime.UtcNow.AddYears(-20),
            }).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        const string code = "abc123";

        using (var scope = Host.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
            var cache = scope.ServiceProvider.GetRequiredService<IDistributedCache>();

            var user = await ctx.Users.FirstAsync(u => u.Email == email);
            user.EmailConfirmed = false;
            await ctx.SaveChangesAsync();

            // Mirrors UserCreatedHandler: the welcome email caches the code
            // under the real e-mail address, never the username.
            await cache.SetStringAsync($"verification_code:{email}", code);
        }

        // The client only knows the username at this point (e.g. the
        // post-login-403 "verify your email" prompt), so it submits that as
        // the `email` query param.
        await Host.Scenario(x =>
        {
            x.Get.Url($"/api/v1/user/verify-email?email={Uri.EscapeDataString(username)}&code={code}");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var verifyScope = Host.Services.CreateScope();
        var verifyCtx = verifyScope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var verifiedUser = await verifyCtx.Users.FirstAsync(u => u.Email == email);
        Assert.That(verifiedUser.EmailConfirmed, Is.True,
            "verify-email must key the cache lookup off the resolved user's e-mail, not the raw identifier the caller supplied, so the code from the original welcome e-mail is found on the first attempt.");
    }

    // ── Account enumeration ─────────────────────────────────────────────────────
    //
    // Both routes are anonymous, and both used to answer differently depending on whether the
    // address belonged to an account: generate-verification-code returned 202 / 200 / 400 "User
    // already verified" for unknown / registered-unverified / registered-verified, and verify-email
    // returned 202 for an unknown address where a live account got a 400. Either one turned an
    // arbitrary email list into a registration census with one unauthenticated GET per address.

    /// <summary>Registers a user and returns (email, username), leaving EmailConfirmed as
    /// <paramref name="confirmed"/> - registration auto-confirms in this harness
    /// (AUTH_REQUIRE_USER_EMAIL_VERIFICATION=false), so the unverified case has to be made.</summary>
    private static async Task<(string Email, string Username)> RegisterAsync(bool confirmed)
    {
        var email = $"enum-{Guid.NewGuid()}@example.com";
        var username = $"user{Guid.NewGuid():N}"[..12];

        await Host.Scenario(x =>
        {
            x.Post.Json(new CreateUserRequest
            {
                Email = email,
                Password = "SecurePass123!",
                Username = username,
                BirthDate = DateTime.UtcNow.AddYears(-20),
            }).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var user = await ctx.Users.FirstAsync(u => u.Email == email);
        user.EmailConfirmed = confirmed;
        await ctx.SaveChangesAsync();

        return (email, username);
    }

    private static async Task SeedCodeAsync(string email, string code)
    {
        using var scope = Host.Services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IDistributedCache>();
        await cache.SetStringAsync($"verification_code:{email}", code);
    }

    private static Task<string> GetAsync(string url) =>
        Host.Scenario(x =>
        {
            x.Get.Url(url);
            x.IgnoreStatusCode();
        }).ContinueWith(t => ResponseFingerprint.OfAsync(t.Result)).Unwrap();

    [Test]
    public async Task GenerateVerificationCode_AnswersIdenticallyForKnownUnverifiedUnknownAndVerified()
    {
        var (unverifiedEmail, _) = await RegisterAsync(confirmed: false);
        var (verifiedEmail, _) = await RegisterAsync(confirmed: true);
        var unknownEmail = $"nobody-{Guid.NewGuid()}@example.com";

        var unverified = await GetAsync(
            $"/api/v1/user/generate-verification-code?email={Uri.EscapeDataString(unverifiedEmail)}");
        var unknown = await GetAsync(
            $"/api/v1/user/generate-verification-code?email={Uri.EscapeDataString(unknownEmail)}");
        var verified = await GetAsync(
            $"/api/v1/user/generate-verification-code?email={Uri.EscapeDataString(verifiedEmail)}");

        Assert.Multiple(() =>
        {
            Assert.That(unknown, Is.EqualTo(unverified),
                "an unregistered address must be answered exactly as a registered unverified one - this " +
                "used to be 202 vs 200.");
            Assert.That(verified, Is.EqualTo(unverified),
                "an already-verified account must be answered exactly as an unverified one - this used " +
                "to be 400 \"User already verified\".");
            Assert.That(unverified, Does.Contain("status: 202"),
                "the uniform answer is Accepted: the caller is told the request was taken, never what it found.");
        });
    }

    [Test]
    public async Task VerifyEmail_AnswersIdenticallyForUnknownWrongCodeMissingCodeAndAlreadyVerified()
    {
        var (liveEmail, _) = await RegisterAsync(confirmed: false);
        await SeedCodeAsync(liveEmail, "111111");

        var (noCodeEmail, _) = await RegisterAsync(confirmed: false);
        var (verifiedEmail, _) = await RegisterAsync(confirmed: true);
        var unknownEmail = $"nobody-{Guid.NewGuid()}@example.com";

        // A live account with an outstanding code, guessed wrong. This is the case the two-request
        // oracle used: request a code first, then submit a wrong one, and only a real account had
        // anything cached to be "wrong" against.
        var wrongCode = await GetAsync(
            $"/api/v1/user/verify-email?email={Uri.EscapeDataString(liveEmail)}&code=999999");
        var unknown = await GetAsync(
            $"/api/v1/user/verify-email?email={Uri.EscapeDataString(unknownEmail)}&code=999999");
        var noCode = await GetAsync(
            $"/api/v1/user/verify-email?email={Uri.EscapeDataString(noCodeEmail)}&code=999999");
        var alreadyVerified = await GetAsync(
            $"/api/v1/user/verify-email?email={Uri.EscapeDataString(verifiedEmail)}&code=999999");

        Assert.Multiple(() =>
        {
            Assert.That(unknown, Is.EqualTo(wrongCode),
                "an unregistered address must be refused exactly as a real account guessing wrong is - " +
                "this used to be 202 vs 400.");
            Assert.That(noCode, Is.EqualTo(wrongCode),
                "\"no code outstanding\" and \"wrong code\" must not be distinguishable: told apart, they " +
                "reveal whether a code request landed on a real account.");
            Assert.That(alreadyVerified, Is.EqualTo(wrongCode),
                "an already-verified account must be refused like everything else - this used to be " +
                "400 \"User already verified\".");
            Assert.That(wrongCode, Does.Contain("status: 400"));
        });
    }

    [Test]
    public async Task VerifyEmail_ExhaustedAttemptsAreIndistinguishableFromAnUnknownAddress()
    {
        var (email, _) = await RegisterAsync(confirmed: false);
        await SeedCodeAsync(email, "111111");

        // OneTimeCodeService destroys the code on the fifth wrong guess. The old "Too many
        // incorrect attempts" wording announced that a code had existed at all, which only a
        // registered address could produce.
        for (var attempt = 0; attempt < OneTimeCodeService.MaxAttempts; attempt++)
        {
            await GetAsync($"/api/v1/user/verify-email?email={Uri.EscapeDataString(email)}&code=999999");
        }

        var exhausted = await GetAsync(
            $"/api/v1/user/verify-email?email={Uri.EscapeDataString(email)}&code=999999");
        var unknown = await GetAsync(
            $"/api/v1/user/verify-email?email={Uri.EscapeDataString($"nobody-{Guid.NewGuid()}@example.com")}&code=999999");

        Assert.That(unknown, Is.EqualTo(exhausted));
    }

    [Test]
    public async Task VerifyEmail_CorrectCodeStillConfirms()
    {
        // The uniform-refusal change must not have made every answer a refusal.
        var (email, _) = await RegisterAsync(confirmed: false);
        await SeedCodeAsync(email, "222222");

        await Host.Scenario(x =>
        {
            x.Get.Url($"/api/v1/user/verify-email?email={Uri.EscapeDataString(email)}&code=222222");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        Assert.That((await ctx.Users.FirstAsync(u => u.Email == email)).EmailConfirmed, Is.True);
    }

    [Test]
    public async Task GenerateVerificationCode_MissingEmail_IsStillRejected()
    {
        // Genuine input validation survives the uniform 202: whether the caller sent an identifier
        // at all does not depend on whose identifier it is.
        await Host.Scenario(x =>
        {
            x.Get.Url("/api/v1/user/generate-verification-code?email=");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    [TearDown]
    public async Task ClearDatabaseAfterTest()
    {
        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

        if (await ctx.Users.AnyAsync())
        {
            ctx.Users.RemoveRange(ctx.Users);
            await ctx.SaveChangesAsync();
        }
    }
}
