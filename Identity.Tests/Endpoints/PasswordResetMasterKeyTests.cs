using System.Net;
using System.Text.Json;
using Alba;
using Identity.Application.Dtos.Request;
using Identity.Application.Services;
using Identity.Domain.ValueObjects;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Tests.Endpoints;

/// <summary>
/// What a password reset does to the account master key.
///
/// <para><b>The bug this pins down.</b> The reset path called <c>ResetPasswordAsync</c> and never
/// touched <c>EncryptedMasterKey</c>. That envelope is sealed under Argon2(old password) - and a
/// reset is, by definition, the case where the user no longer has the old password. So every backup
/// blob and the account identity key became permanently unopenable, silently, at the exact moment
/// the user was trying to recover their account. Nothing failed; the loss only surfaced at the first
/// restore attempt, potentially months later.</para>
///
/// <para>The fix is to wrap the master key twice - once under the password, once under a recovery
/// code - so a reset invalidates one wrapping and the client re-wraps from the other. These tests
/// cover both branches: the account that saved a recovery code, and the account that did not.</para>
/// </summary>
[TestFixture]
public class PasswordResetMasterKeyTests
{
    private const string OldPassword = "SecurePass123!";
    private const string NewPassword = "EvenMoreSecure456!";

    private static IAlbaHost Host => AppFixture.Host;

    private static async Task<(string Username, string Email, string Token)> RegisterAsync(string prefix)
    {
        var username = $"{prefix}{Guid.NewGuid():N}"[..15];
        var email = $"{username}@example.com";

        await Host.Scenario(x =>
        {
            x.Post.Json(new CreateUserRequest
            {
                Email = email,
                Password = OldPassword,
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
                ["password"] = OldPassword,
                ["client_id"] = "echo",
            }).ToUrl("/connect/token");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await tokenResult.ReadAsJsonAsync<JsonElement>();
        return (username, email, body.GetProperty("access_token").GetString()!);
    }

    /// <summary>Seeds the wrappings directly - the client-side Argon2 derivation is not what is
    /// under test here, only what the reset does to whatever is stored.</summary>
    private static async Task SeedMasterKeyAsync(string username, bool withRecoveryCodeWrapping)
    {
        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var user = await ctx.Users.FirstAsync(u => u.UserName == username);

        user.EncryptedMasterKey = new EncryptedMasterKey
        {
            CipherText = [1, 1, 1], Salt = [2], Iv = [3], Version = 1, Kdf = "argon2id",
        };
        user.RecoveryCodeWrappedMasterKey = withRecoveryCodeWrapping
            ? new EncryptedMasterKey { CipherText = [4, 4, 4], Salt = [5], Iv = [6], Version = 1, Kdf = "argon2id" }
            : null;

        await ctx.SaveChangesAsync();
    }

    private static async Task<JsonElement> ResetPasswordAsync(string email)
    {
        using (var scope = Host.Services.CreateScope())
        {
            var cache = scope.ServiceProvider.GetRequiredService<IDistributedCache>();
            await PasswordResetCodeService.GetOrCreateCodeAsync(cache, email);
        }

        string code;
        using (var scope = Host.Services.CreateScope())
        {
            var cache = scope.ServiceProvider.GetRequiredService<IDistributedCache>();
            code = (await cache.GetStringAsync($"password_reset_code:{email}"))!;
        }

        var result = await Host.Scenario(x =>
        {
            x.Post.Json(new ResetPasswordDto { Email = email, Code = code, NewPassword = NewPassword })
                .ToUrl("/api/v1/user/reset-password");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        return await result.ReadAsJsonAsync<JsonElement>();
    }

    private static async Task<(DateTimeOffset? InvalidatedAt, bool HasRecoveryWrapping)> ReadStateAsync(string username)
    {
        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var user = await ctx.Users.AsNoTracking().FirstAsync(u => u.UserName == username);
        return (user.MasterKeyPasswordWrappingInvalidatedAt, user.RecoveryCodeWrappedMasterKey is not null);
    }

    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ResetPassword_MarksThePasswordWrappingStale()
    {
        var (username, email, _) = await RegisterAsync("resetmk");
        await SeedMasterKeyAsync(username, withRecoveryCodeWrapping: true);

        await ResetPasswordAsync(email);

        // Nothing server-side can re-wrap it - the server never sees the master key - so recording
        // that it happened is the only honest thing available, and it is what turns a silent loss
        // into an instruction the client can act on.
        var state = await ReadStateAsync(username);
        Assert.That(state.InvalidatedAt, Is.Not.Null);
    }

    [Test]
    public async Task ResetPassword_WithARecoveryCodeWrapping_TellsTheClientToRewrap()
    {
        var (username, email, _) = await RegisterAsync("resetmkok");
        await SeedMasterKeyAsync(username, withRecoveryCodeWrapping: true);

        var body = await ResetPasswordAsync(email);

        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("masterKeyRewrapRequired").GetBoolean(), Is.True);
            // Nothing is lost: the recovery code still opens the key, so the client can re-seal it
            // under the new password.
            Assert.That(body.GetProperty("encryptedHistoryRecoverable").GetBoolean(), Is.True);
        });
    }

    [Test]
    public async Task ResetPassword_WithoutARecoveryCodeWrapping_ReportsTheHistoryAsLost()
    {
        var (username, email, _) = await RegisterAsync("resetmkbad");
        await SeedMasterKeyAsync(username, withRecoveryCodeWrapping: false);

        var body = await ResetPasswordAsync(email);

        Assert.Multiple(() =>
        {
            // There is nothing to re-wrap *from*, so asking the client to try would be pointless.
            Assert.That(body.GetProperty("masterKeyRewrapRequired").GetBoolean(), Is.False);
            // This is a completed loss, not a warning about the future. Every backup blob on the
            // account is unopenable from this moment and no correct password will change that -
            // saying so beats letting it appear to work until the first restore fails.
            Assert.That(body.GetProperty("encryptedHistoryRecoverable").GetBoolean(), Is.False);
        });
    }

    [Test]
    public async Task ResetPassword_ForAnAccountWithNoMasterKey_ChangesNothing()
    {
        var (username, email, _) = await RegisterAsync("resetmknone");

        var body = await ResetPasswordAsync(email);

        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("masterKeyRewrapRequired").GetBoolean(), Is.False);
            Assert.That(body.GetProperty("encryptedHistoryRecoverable").GetBoolean(), Is.True);
        });
        Assert.That((await ReadStateAsync(username)).InvalidatedAt, Is.Null);
    }

    [Test]
    public async Task Rewrapping_AfterAReset_RestoresThePasswordWrappingWithoutRotating()
    {
        var (username, email, _) = await RegisterAsync("rewrapmk");
        await SeedMasterKeyAsync(username, withRecoveryCodeWrapping: true);
        await ResetPasswordAsync(email);

        var tokenResult = await Host.Scenario(x =>
        {
            x.Post.FormData(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = username,
                ["password"] = NewPassword,
                ["client_id"] = "echo",
            }).ToUrl("/connect/token");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        var token = (await tokenResult.ReadAsJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new RewrapMasterKeyDto
            {
                Version = 1,
                PasswordWrapping = new MasterKeyWrappingDto
                {
                    CipherText = [7, 7, 7], Salt = [8], Iv = [9], Iterations = 3,
                    MemoryKiB = 65536, Parallelism = 1,
                },
            }).ToUrl("/api/v1/backup/recovery-key/rewrap-password");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var user = await ctx.Users.AsNoTracking().FirstAsync(u => u.UserName == username);

        Assert.Multiple(() =>
        {
            Assert.That(user.MasterKeyPasswordWrappingInvalidatedAt, Is.Null);
            Assert.That(user.EncryptedMasterKey!.CipherText, Is.EqualTo(new byte[] { 7, 7, 7 }));
            // The version is untouched, which is the whole point: every blob sealed under version 1
            // is still readable. Re-wrapping is not rotation.
            Assert.That(user.EncryptedMasterKey.Version, Is.EqualTo(1));
            Assert.That(user.RecoveryCodeWrappedMasterKey!.CipherText, Is.EqualTo(new byte[] { 4, 4, 4 }));
        });
    }

    [Test]
    public async Task Rewrapping_AtTheWrongVersion_IsRefused()
    {
        var (username, _, token) = await RegisterAsync("rewrapver");
        await SeedMasterKeyAsync(username, withRecoveryCodeWrapping: true);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new RewrapMasterKeyDto
            {
                Version = 7,
                PasswordWrapping = new MasterKeyWrappingDto
                {
                    CipherText = [7], Salt = [8], Iv = [9], Iterations = 3, MemoryKiB = 65536, Parallelism = 1,
                },
            }).ToUrl("/api/v1/backup/recovery-key/rewrap-password");
            x.StatusCodeShouldBe(HttpStatusCode.Conflict);
        });

        // A version mismatch means the client holds a master key the account has moved on from.
        // Writing it would make every blob sealed under the current version unopenable.
        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var user = await ctx.Users.AsNoTracking().FirstAsync(u => u.UserName == username);
        Assert.That(user.EncryptedMasterKey!.CipherText, Is.EqualTo(new byte[] { 1, 1, 1 }));
    }
}
