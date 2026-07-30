using System.Net;
using Alba;
using Identity.Application.Dtos.Request;
using Identity.Application.Services;
using Identity.Domain.Aggregates;
using Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Tests.Endpoints;

[TestFixture]
public class PasswordResetEndpointTests
{
    private const string Password = "SecurePass123!";

    private static IAlbaHost Host => AppFixture.Host;

    private static async Task<string> RegisterAsync(string username)
    {
        var email = $"{username}-{Guid.NewGuid():N}@example.com";
        await Host.Scenario(x =>
        {
            x.Post.Json(new CreateUserRequest
            {
                Email = email,
                Password = Password,
                Username = username,
                BirthDate = DateTime.UtcNow.AddYears(-20),
            }).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        return email;
    }

    // ── request-password-reset ────────────────────────────────────────────────

    [Test]
    public async Task RequestPasswordReset_UnknownEmail_ReturnsAcceptedWithoutLeakingExistence()
    {
        await Host.Scenario(x =>
        {
            x.Get.Url($"/api/v1/user/request-password-reset?email={Uri.EscapeDataString($"nosuchuser-{Guid.NewGuid():N}@example.com")}");
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });
    }

    /// <summary>Regression test for a bug found while writing these tests: EmailService (shared
    /// Messaging infra) never initializes its Graph client when
    /// AUTH_REQUIRE_USER_EMAIL_VERIFICATION=false (see Messaging/EmailService.cs's constructor),
    /// so SendEmailAsync threw a NullReferenceException for any *existing* account - unlike the
    /// welcome-email path (UserCreatedHandler), which already guards against this by skipping the
    /// call entirely under that flag. Since request-password-reset's whole point is to look
    /// identical whether or not the account exists, an unhandled 500 here also defeats that by
    /// distinguishing "exists" (crash) from "doesn't exist" (202). Fixed by catching the mail
    /// fault in PasswordResetEndpoint and still returning Accepted - the reset code is already
    /// cached, so a resend can succeed once mail delivery is available.</summary>
    [Test]
    public async Task RequestPasswordReset_KnownEmail_ReturnsAcceptedEvenIfMailDeliveryFails()
    {
        var username = $"pwreqok{Guid.NewGuid():N}"[..15];
        var email = await RegisterAsync(username);

        await Host.Scenario(x =>
        {
            x.Get.Url($"/api/v1/user/request-password-reset?email={Uri.EscapeDataString(email)}");
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        // The reset code must still have been minted and cached despite the mail-delivery fault,
        // so the user could complete the flow through some other channel (support, retry once
        // mail is configured, etc.) instead of being left with no code at all.
        using var scope = Host.Services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IDistributedCache>();
        Assert.That(await cache.GetStringAsync($"password_reset_code:{email}"), Is.Not.Null);
    }

    // ── reset-password ─────────────────────────────────────────────────────────
    //
    // These seed the reset code directly into the cache (mirroring how
    // UserVerificationEndpointTests seeds the verification code) rather than going through
    // request-password-reset, since that endpoint unconditionally calls EmailService.SendEmailAsync
    // - see the bug noted in RequestPasswordReset_KnownEmail_DocumentsEmailServiceNullRefBug below.

    private static async Task SeedResetCodeAsync(string email, string code)
    {
        using var scope = Host.Services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IDistributedCache>();
        await cache.SetStringAsync($"password_reset_code:{email}", code);
    }

    [Test]
    public async Task ResetPassword_ValidCode_ChangesPasswordAndConsumesCode()
    {
        var username = $"pwresetok{Guid.NewGuid():N}"[..15];
        var email = await RegisterAsync(username);
        const string code = "abc123";
        const string newPassword = "BrandNewPass456!";
        await SeedResetCodeAsync(email, code);

        await Host.Scenario(x =>
        {
            x.Post.Json(new ResetPasswordDto { Email = email, Code = code, NewPassword = newPassword })
                .ToUrl("/api/v1/user/reset-password");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var scope = Host.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await manager.FindByEmailAsync(email);
        Assert.That(await manager.CheckPasswordAsync(user!, newPassword), Is.True);

        // Code must be single-use.
        var cache = scope.ServiceProvider.GetRequiredService<IDistributedCache>();
        Assert.That(await cache.GetStringAsync($"password_reset_code:{email}"), Is.Null);
    }

    [Test]
    public async Task ResetPassword_InvalidCode_ReturnsBadRequestAndLeavesPasswordUnchanged()
    {
        var username = $"pwresetbad{Guid.NewGuid():N}"[..15];
        var email = await RegisterAsync(username);
        await SeedResetCodeAsync(email, "correct1");

        await Host.Scenario(x =>
        {
            x.Post.Json(new ResetPasswordDto { Email = email, Code = "wrongcode", NewPassword = "SomeNewPass789!" })
                .ToUrl("/api/v1/user/reset-password");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });

        using var scope = Host.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await manager.FindByEmailAsync(email);
        Assert.That(await manager.CheckPasswordAsync(user!, Password), Is.True, "Original password must still work");
    }

    [Test]
    public async Task ResetPassword_ExpiredOrMissingCode_ReturnsBadRequest()
    {
        var username = $"pwresetexpired{Guid.NewGuid():N}"[..15];
        var email = await RegisterAsync(username);
        // No code ever seeded - simulates the cache entry having expired (15-minute TTL) or never existing.

        await Host.Scenario(x =>
        {
            x.Post.Json(new ResetPasswordDto { Email = email, Code = "anything", NewPassword = "SomeNewPass789!" })
                .ToUrl("/api/v1/user/reset-password");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    [Test]
    public async Task ResetPassword_UnknownEmail_ReturnsBadRequest()
    {
        await Host.Scenario(x =>
        {
            x.Post.Json(new ResetPasswordDto
            {
                Email = $"nosuchuser-{Guid.NewGuid():N}@example.com",
                Code = "whatever",
                NewPassword = "SomeNewPass789!",
            }).ToUrl("/api/v1/user/reset-password");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    [Test]
    public async Task ResetPassword_WeakNewPassword_ReturnsValidationProblem()
    {
        var username = $"pwresetweak{Guid.NewGuid():N}"[..15];
        var email = await RegisterAsync(username);
        const string code = "abc123";
        await SeedResetCodeAsync(email, code);

        await Host.Scenario(x =>
        {
            x.Post.Json(new ResetPasswordDto { Email = email, Code = code, NewPassword = "a" })
                .ToUrl("/api/v1/user/reset-password");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    // ── PasswordResetCodeService reuse behavior, exercised through the real HTTP endpoint ──────

    [Test]
    public async Task ResetPassword_CodeCanOnlyBeUsedOnce()
    {
        var username = $"pwresetreuse{Guid.NewGuid():N}"[..15];
        var email = await RegisterAsync(username);
        const string code = "abc123";
        await SeedResetCodeAsync(email, code);

        await Host.Scenario(x =>
        {
            x.Post.Json(new ResetPasswordDto { Email = email, Code = code, NewPassword = "FirstNewPass123!" })
                .ToUrl("/api/v1/user/reset-password");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        // Replaying the same (now-consumed) code must fail even though it was valid moments ago.
        await Host.Scenario(x =>
        {
            x.Post.Json(new ResetPasswordDto { Email = email, Code = code, NewPassword = "SecondNewPass456!" })
                .ToUrl("/api/v1/user/reset-password");
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
