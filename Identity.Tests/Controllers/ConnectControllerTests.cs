using System.Net;
using System.Text.Json;
using Alba;
using Identity.Application.Dtos.Request;
using Identity.Application.Services.Steam;
using Identity.Contracts.Bus.Commands;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Aggregates;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Identity.Tests.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace Identity.Tests.Controllers;

/// <summary>
/// Covers the OpenIddict token endpoint (POST /connect/token): password grant (incl.
/// </summary>
[TestFixture]
public class ConnectControllerTests
{
    private const string Password = "SecurePass123!";

    private static IAlbaHost Host => AppFixture.Host;

    private static CreateUserRequest ValidRequest(string username, string? email = null) => new()
    {
        Email = email ?? $"{username}-{Guid.NewGuid():N}@example.com",
        Password = Password,
        Username = username,
        BirthDate = DateTime.UtcNow.AddYears(-20),
    };

    private static async Task RegisterAsync(CreateUserRequest request)
    {
        await Host.Scenario(x =>
        {
            x.Post.Json(request).ToUrl("/api/v1/authentication/register");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
    }

    private static Task<IScenarioResult> RequestTokenAsync(Dictionary<string, string> form, HttpStatusCode expectStatus)
    {
        return Host.Scenario(x =>
        {
            x.Post.FormData(form).ToUrl("/connect/token");
            x.StatusCodeShouldBe(expectStatus);
        });
    }

    private static async Task<T> RunInScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Host.Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    // ── Password grant: happy path ──────────────────────────────────────────

    [Test]
    public async Task Exchange_PasswordGrant_ValidCredentials_ReturnsAccessToken()
    {
        var username = $"connok{Guid.NewGuid():N}"[..15];
        await RegisterAsync(ValidRequest(username));

        var result = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = Password,
            ["client_id"] = "echo",
        }, HttpStatusCode.OK);

        var body = await result.ReadAsJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("access_token").GetString(), Is.Not.Null.And.Not.Empty);
        Assert.That(body.GetProperty("token_type").GetString(), Is.EqualTo("Bearer"));
    }

    [Test]
    public async Task Exchange_PasswordGrant_WrongPassword_ReturnsUnauthorized()
    {
        var username = $"connwrongpw{Guid.NewGuid():N}"[..15];
        await RegisterAsync(ValidRequest(username));

        await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = "TotallyWrongPassword!",
            ["client_id"] = "echo",
        }, HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Exchange_PasswordGrant_UnknownUser_ReturnsNotFound()
    {
        await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = $"nosuchuser{Guid.NewGuid():N}",
            ["password"] = Password,
            ["client_id"] = "echo",
        }, HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Regression test for a bug found while writing these tests: ConnectController used to call
    /// `Forbid("message")`, which ASP.NET Core's ControllerBase.Forbid(params string[]
    /// authenticationSchemes) interprets as an authentication scheme name, not a display message.
    /// </summary>
    [Test]
    public async Task Exchange_PasswordGrant_UserNotAllowedToSignIn_Returns403WithoutThrowing()
    {
        var username = $"connbanned{Guid.NewGuid():N}"[..15];
        await RegisterAsync(ValidRequest(username));

        await RunInScopeAsync(async sp =>
        {
            var ctx = sp.GetRequiredService<MicroserviceContext>();
            var user = await ctx.Users.FirstAsync(u => u.UserName == username);
            user.Status = UserStatus.Banned;
            await ctx.SaveChangesAsync();
            return true;
        });

        var result = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = Password,
            ["client_id"] = "echo",
        }, HttpStatusCode.Forbidden);

        var body = await result.ReadAsTextAsync();
        Assert.That(body, Does.Contain("not allowed to sign in"));
    }

    // ── Password grant: MFA ─────────────────────────────────────────────────

    private static async Task<string> EnableMfaAsync(string username)
    {
        return await RunInScopeAsync(async sp =>
        {
            var manager = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await manager.FindByNameAsync(username);
            await manager.ResetAuthenticatorKeyAsync(user!);
            var key = await manager.GetAuthenticatorKeyAsync(user!);
            await manager.SetTwoFactorEnabledAsync(user!, true);
            return key!;
        });
    }

    [Test]
    public async Task Exchange_PasswordGrant_MfaEnabledNoCode_ReturnsMfaRequired()
    {
        var username = $"connmfareq{Guid.NewGuid():N}"[..15];
        await RegisterAsync(ValidRequest(username));
        await EnableMfaAsync(username);

        var result = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = Password,
            ["client_id"] = "echo",
        }, HttpStatusCode.Unauthorized);

        var body = await result.ReadAsTextAsync();
        Assert.That(body, Does.Contain("mfa_required"));
    }

    [Test]
    public async Task Exchange_PasswordGrant_MfaEnabledWrongCode_ReturnsMfaInvalid()
    {
        var username = $"connmfabad{Guid.NewGuid():N}"[..15];
        await RegisterAsync(ValidRequest(username));
        await EnableMfaAsync(username);

        var result = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = Password,
            ["client_id"] = "echo",
            ["mfa_code"] = "000000",
        }, HttpStatusCode.Unauthorized);

        var body = await result.ReadAsTextAsync();
        Assert.That(body, Does.Contain("mfa_invalid"));
    }

    [Test]
    public async Task Exchange_PasswordGrant_MfaEnabledValidTotpCode_ReturnsAccessToken()
    {
        var username = $"connmfaok{Guid.NewGuid():N}"[..15];
        await RegisterAsync(ValidRequest(username));
        var key = await EnableMfaAsync(username);
        var code = TotpHelper.GenerateCode(key);

        var result = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = Password,
            ["client_id"] = "echo",
            ["mfa_code"] = code,
        }, HttpStatusCode.OK);

        var body = await result.ReadAsJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("access_token").GetString(), Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task Exchange_PasswordGrant_MfaEnabledRecoveryCode_ReturnsAccessToken()
    {
        var username = $"connmfarec{Guid.NewGuid():N}"[..15];
        await RegisterAsync(ValidRequest(username));
        await EnableMfaAsync(username);

        var recoveryCode = await RunInScopeAsync(async sp =>
        {
            var manager = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await manager.FindByNameAsync(username);
            var codes = await manager.GenerateNewTwoFactorRecoveryCodesAsync(user!, 8);
            return codes!.First();
        });

        var result = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = Password,
            ["client_id"] = "echo",
            ["mfa_code"] = recoveryCode,
        }, HttpStatusCode.OK);

        var body = await result.ReadAsJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("access_token").GetString(), Is.Not.Null.And.Not.Empty);
    }

    // ── Refresh token grant ──────────────────────────────────────────────────

    [Test]
    public async Task Exchange_RefreshTokenGrant_ValidRefreshToken_ReturnsNewAccessToken()
    {
        var username = $"connrefresh{Guid.NewGuid():N}"[..15];
        await RegisterAsync(ValidRequest(username));

        var initial = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = Password,
            ["client_id"] = "echo",
            ["scope"] = "offline_access",
        }, HttpStatusCode.OK);
        var initialBody = await initial.ReadAsJsonAsync<JsonElement>();
        var refreshToken = initialBody.GetProperty("refresh_token").GetString();
        Assert.That(refreshToken, Is.Not.Null.And.Not.Empty, "offline_access scope should mint a refresh token");

        var refreshed = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken!,
            ["client_id"] = "echo",
        }, HttpStatusCode.OK);

        var refreshedBody = await refreshed.ReadAsJsonAsync<JsonElement>();
        Assert.That(refreshedBody.GetProperty("access_token").GetString(), Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task Exchange_RefreshTokenGrant_UserNoLongerAllowedToSignIn_Returns403()
    {
        var username = $"connrefreshban{Guid.NewGuid():N}"[..15];
        await RegisterAsync(ValidRequest(username));

        var initial = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = Password,
            ["client_id"] = "echo",
            ["scope"] = "offline_access",
        }, HttpStatusCode.OK);
        var refreshToken = (await initial.ReadAsJsonAsync<JsonElement>()).GetProperty("refresh_token").GetString();

        await RunInScopeAsync(async sp =>
        {
            var ctx = sp.GetRequiredService<MicroserviceContext>();
            var user = await ctx.Users.FirstAsync(u => u.UserName == username);
            user.Status = UserStatus.Banned;
            await ctx.SaveChangesAsync();
            return true;
        });

        await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken!,
            ["client_id"] = "echo",
        }, HttpStatusCode.Forbidden);
    }

    // ── Steam grant ───────────────────────────────────────────────────────────

    [Test]
    public async Task Exchange_SteamGrant_ValidTicket_ReturnsAccessToken()
    {
        var username = $"connsteam{Guid.NewGuid():N}"[..15];
        await RegisterAsync(ValidRequest(username));
        var ticket = Guid.NewGuid().ToString("N");

        await RunInScopeAsync(async sp =>
        {
            var ctx = sp.GetRequiredService<MicroserviceContext>();
            var cache = sp.GetRequiredService<IDistributedCache>();
            var user = await ctx.Users.FirstAsync(u => u.UserName == username);
            await cache.SetStringAsync(SteamOpenIdService.LoginTicketCacheKey(ticket), user.Id);
            return true;
        });

        var result = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = SteamOpenIdService.SteamGrantType,
            [SteamOpenIdService.TicketParameter] = ticket,
            ["client_id"] = "echo",
        }, HttpStatusCode.OK);

        var body = await result.ReadAsJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("access_token").GetString(), Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task Exchange_SteamGrant_TicketConsumedAfterUse_SecondAttemptFails()
    {
        var username = $"connsteam2{Guid.NewGuid():N}"[..15];
        await RegisterAsync(ValidRequest(username));
        var ticket = Guid.NewGuid().ToString("N");

        await RunInScopeAsync(async sp =>
        {
            var ctx = sp.GetRequiredService<MicroserviceContext>();
            var cache = sp.GetRequiredService<IDistributedCache>();
            var user = await ctx.Users.FirstAsync(u => u.UserName == username);
            await cache.SetStringAsync(SteamOpenIdService.LoginTicketCacheKey(ticket), user.Id);
            return true;
        });

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = SteamOpenIdService.SteamGrantType,
            [SteamOpenIdService.TicketParameter] = ticket,
            ["client_id"] = "echo",
        };

        await RequestTokenAsync(form, HttpStatusCode.OK);
        // The ticket is single-use - the identical replayed request must now fail.
        await RequestTokenAsync(form, HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Exchange_SteamGrant_MissingTicketParameter_ReturnsBadRequest()
    {
        await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = SteamOpenIdService.SteamGrantType,
            ["client_id"] = "echo",
        }, HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Exchange_SteamGrant_UnknownTicket_ReturnsUnauthorized()
    {
        await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = SteamOpenIdService.SteamGrantType,
            [SteamOpenIdService.TicketParameter] = Guid.NewGuid().ToString("N"),
            ["client_id"] = "echo",
        }, HttpStatusCode.Unauthorized);
    }

    // ── Client credentials grant (bots) ──────────────────────────────────────

    [Test]
    public async Task Exchange_ClientCredentialsGrant_ActiveBot_ReturnsAccessToken()
    {
        var botId = $"bot_{Guid.NewGuid():N}";
        var clientSecret = await RunInScopeAsync(async sp =>
        {
            var bus = sp.GetRequiredService<IMessageBus>();
            var response = await bus.InvokeAsync<CreateBotAccountResponse>(new CreateBotAccountCommand
            {
                BotUserId = botId,
                Name = "TestBot",
            });
            return response.ClientSecret;
        });

        var result = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = botId,
            ["client_secret"] = clientSecret,
        }, HttpStatusCode.OK);

        var body = await result.ReadAsJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("access_token").GetString(), Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task Exchange_ClientCredentialsGrant_DisabledBot_Returns403()
    {
        var botId = $"bot_{Guid.NewGuid():N}";
        var clientSecret = await RunInScopeAsync(async sp =>
        {
            var bus = sp.GetRequiredService<IMessageBus>();
            var response = await bus.InvokeAsync<CreateBotAccountResponse>(new CreateBotAccountCommand
            {
                BotUserId = botId,
                Name = "DisabledBot",
            });
            return response.ClientSecret;
        });

        await RunInScopeAsync(async sp =>
        {
            var ctx = sp.GetRequiredService<MicroserviceContext>();
            var bot = await ctx.Users.FirstAsync(u => u.Id == botId);
            bot.Status = UserStatus.Banned;
            await ctx.SaveChangesAsync();
            return true;
        });

        await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = botId,
            ["client_secret"] = clientSecret,
        }, HttpStatusCode.Forbidden);
    }

    // ── Unsupported grant type ────────────────────────────────────────────────

    [Test]
    public async Task Exchange_UnsupportedGrantType_ReturnsBadRequest()
    {
        // OpenIddict itself rejects grant types that weren't enabled via AllowXFlow() in
        // Program.cs before the request ever reaches ConnectController's final `else` branch,
        // so this still exercises the "not supported" outcome end-to-end.
        await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["client_id"] = "echo",
            ["device_code"] = "whatever",
        }, HttpStatusCode.BadRequest);
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
