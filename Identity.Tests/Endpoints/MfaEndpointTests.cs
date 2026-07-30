using System.Net;
using System.Text.Json;
using Alba;
using Identity.Application.Dtos.Request;
using Identity.Domain.Aggregates;
using Identity.Infrastructure.Persistence;
using Identity.Tests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Tests.Endpoints;

/// <summary>Covers MfaEndpoint's enroll/enable/disable/recovery-codes flow end to end via HTTP,
/// using a real Bearer token obtained through /connect/token (same approach as
/// ConnectControllerTests) so [Authorize] is exercised for real rather than bypassed.</summary>
[TestFixture]
public class MfaEndpointTests
{
    private const string Password = "SecurePass123!";

    private static IAlbaHost Host => AppFixture.Host;

    private static async Task<string> RegisterAndLoginAsync(string username)
    {
        await Host.Scenario(x =>
        {
            x.Post.Json(new CreateUserRequest
            {
                Email = $"{username}-{Guid.NewGuid():N}@example.com",
                Password = Password,
                Username = username,
                BirthDate = DateTime.UtcNow.AddYears(-20),
            }).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var tokenResult = await Host.Scenario(x =>
        {
            x.Post.FormData(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = username,
                ["password"] = Password,
                ["client_id"] = "echo",
            }).ToUrl("/connect/token");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await tokenResult.ReadAsJsonAsync<JsonElement>();
        return body.GetProperty("access_token").GetString()!;
    }

    // ── Enroll ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Enroll_Authenticated_ReturnsSecretAndOtpAuthUri()
    {
        var username = $"mfaenroll{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Url("/api/v1/user/mfa/enroll");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("secret").GetString(), Is.Not.Null.And.Not.Empty);
        var otpUri = body.GetProperty("otpAuthUri").GetString();
        Assert.That(otpUri, Does.StartWith("otpauth://totp/"));
        Assert.That(otpUri, Does.Contain("issuer=Venta"));
    }

    [Test]
    public async Task Enroll_WithoutBearerToken_ReturnsUnauthorized()
    {
        await Host.Scenario(x =>
        {
            x.Post.Url("/api/v1/user/mfa/enroll");
            x.StatusCodeShouldBe(HttpStatusCode.Unauthorized);
        });
    }

    [Test]
    public async Task Enroll_AlreadyEnabled_ReturnsBadRequest()
    {
        var username = $"mfareenroll{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);

        using (var scope = Host.Services.CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await manager.FindByNameAsync(username);
            await manager.ResetAuthenticatorKeyAsync(user!);
            await manager.SetTwoFactorEnabledAsync(user!, true);
        }

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Url("/api/v1/user/mfa/enroll");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    // ── Enable ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Enable_ValidCode_EnablesMfaAndReturnsEightRecoveryCodes()
    {
        var username = $"mfaenable{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);

        var enrollResult = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Url("/api/v1/user/mfa/enroll");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        var secret = (await enrollResult.ReadAsJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
        var code = TotpHelper.GenerateCode(secret);

        var enableResult = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new EnableMfaDto { Code = code }).ToUrl("/api/v1/user/mfa/enable");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await enableResult.ReadAsJsonAsync<JsonElement>();
        var recoveryCodes = body.GetProperty("recoveryCodes").EnumerateArray().ToList();
        Assert.That(recoveryCodes, Has.Count.EqualTo(8));

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var user = await ctx.Users.FirstAsync(u => u.UserName == username);
        Assert.That(user.TwoFactorEnabled, Is.True);
    }

    [Test]
    public async Task Enable_WrongCode_ReturnsBadRequestAndDoesNotEnableMfa()
    {
        var username = $"mfaenablebad{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Url("/api/v1/user/mfa/enroll");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new EnableMfaDto { Code = "000000" }).ToUrl("/api/v1/user/mfa/enable");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var user = await ctx.Users.FirstAsync(u => u.UserName == username);
        Assert.That(user.TwoFactorEnabled, Is.False);
    }

    // ── Disable ─────────────────────────────────────────────────────────────

    private static async Task EnableMfaDirectlyAsync(string username)
    {
        using var scope = Host.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await manager.FindByNameAsync(username);
        await manager.ResetAuthenticatorKeyAsync(user!);
        await manager.SetTwoFactorEnabledAsync(user!, true);
    }

    [Test]
    public async Task Disable_CorrectPassword_DisablesMfa()
    {
        var username = $"mfadisable{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);
        await EnableMfaDirectlyAsync(username);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new DisableMfaDto { Password = Password }).ToUrl("/api/v1/user/mfa/disable");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var user = await ctx.Users.FirstAsync(u => u.UserName == username);
        Assert.That(user.TwoFactorEnabled, Is.False);
    }

    [Test]
    public async Task Disable_WrongPassword_ReturnsBadRequestAndLeavesMfaEnabled()
    {
        var username = $"mfadisablebad{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);
        await EnableMfaDirectlyAsync(username);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new DisableMfaDto { Password = "WrongPassword!" }).ToUrl("/api/v1/user/mfa/disable");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var user = await ctx.Users.FirstAsync(u => u.UserName == username);
        Assert.That(user.TwoFactorEnabled, Is.True);
    }

    // ── Recovery codes regeneration ───────────────────────────────────────────

    [Test]
    public async Task RegenerateRecoveryCodes_MfaEnabledCorrectPassword_ReturnsEightNewCodes()
    {
        var username = $"mfarecgen{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);
        await EnableMfaDirectlyAsync(username);

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new RegenerateMfaRecoveryCodesDto { Password = Password }).ToUrl("/api/v1/user/mfa/recovery-codes");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("recoveryCodes").EnumerateArray().ToList(), Has.Count.EqualTo(8));
    }

    [Test]
    public async Task RegenerateRecoveryCodes_MfaNotEnabled_ReturnsBadRequest()
    {
        var username = $"mfarecnotenabled{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new RegenerateMfaRecoveryCodesDto { Password = Password }).ToUrl("/api/v1/user/mfa/recovery-codes");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    [Test]
    public async Task RegenerateRecoveryCodes_WrongPassword_ReturnsBadRequest()
    {
        var username = $"mfarecwrongpw{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);
        await EnableMfaDirectlyAsync(username);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new RegenerateMfaRecoveryCodesDto { Password = "WrongPassword!" }).ToUrl("/api/v1/user/mfa/recovery-codes");
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
