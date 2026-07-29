using System.Net;
using Alba;
using Identity.Application.Dtos.Request;
using Identity.Infrastructure.Persistence;
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
            x.StatusCodeShouldBe(HttpStatusCode.OK);
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
            x.StatusCodeShouldBe(HttpStatusCode.OK);
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
