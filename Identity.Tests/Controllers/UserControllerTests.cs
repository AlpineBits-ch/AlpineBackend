using System.Net;
using System.Text.Json;
using Alba;
using Identity.Application.Dtos.Request;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Tests.Controllers;

/// <summary>Covers UserController's self-service endpoints, with emphasis on the account-deletion
/// grace-period flow (RequestDeletionAsync/CancelDeletionAsync), which wraps
/// ApplicationUser.RequestDeletion/CancelDeletionRequest - already unit-tested at the domain level
/// in ApplicationUserTests - behind real HTTP + auth + persistence.</summary>
[TestFixture]
public class UserControllerTests
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

    // ── self/GET ────────────────────────────────────────────────────────────

    [Test]
    public async Task GetSelf_Authenticated_ReturnsOwnUser()
    {
        var username = $"selfget{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url("/api/v1/users/self");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("id").GetString(), Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task GetSelf_WithoutBearerToken_ReturnsUnauthorized()
    {
        await Host.Scenario(x =>
        {
            x.Get.Url("/api/v1/users/self");
            x.StatusCodeShouldBe(HttpStatusCode.Unauthorized);
        });
    }

    // ── self DELETE / cancel-deletion ──────────────────────────────────────────

    [Test]
    public async Task RequestDeletion_Authenticated_StartsGracePeriodAndBlocksSignIn()
    {
        var username = $"userdel{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Delete.Url("/api/v1/users/self");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();
        Assert.That(body.TryGetProperty("purgeScheduledAt", out _), Is.True);

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var user = await ctx.Users.FirstAsync(u => u.UserName == username);
        Assert.That(user.Status, Is.EqualTo(UserStatus.PendingDeletion));
        Assert.That(user.PurgeScheduledAt, Is.Not.Null);
    }

    [Test]
    public async Task CancelDeletion_AfterRequestingDeletion_RevertsToActive()
    {
        var username = $"userdelcancel{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Delete.Url("/api/v1/users/self");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Url("/api/v1/users/self/cancel-deletion");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var user = await ctx.Users.FirstAsync(u => u.UserName == username);
        Assert.That(user.Status, Is.EqualTo(UserStatus.Active));
        Assert.That(user.PurgeScheduledAt, Is.Null);
    }

    [Test]
    public async Task CancelDeletion_WithoutPendingDeletion_ReturnsConflict()
    {
        var username = $"userdelnopending{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Url("/api/v1/users/self/cancel-deletion");
            x.StatusCodeShouldBe(HttpStatusCode.Conflict);
        });
    }

    // ── self/settings ───────────────────────────────────────────────────────

    [Test]
    public async Task GetSettings_NewUser_ReturnsEmptyObject()
    {
        var username = $"usersettingsget{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url("/api/v1/users/self/settings");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();
        Assert.That(body.ValueKind, Is.EqualTo(JsonValueKind.Object));
        Assert.That(body.EnumerateObject(), Is.Empty);
    }

    [Test]
    public async Task PutSettings_ThenGet_RoundTripsStoredJson()
    {
        var username = $"usersettingsput{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.RawJson("""{"theme":"dark"}""");
            x.Put.Url("/api/v1/users/self/settings");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url("/api/v1/users/self/settings");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("theme").GetString(), Is.EqualTo("dark"));
    }

    // ── self/master ─────────────────────────────────────────────────────────

    private static CreateMasterKeyDto MasterKeyDto(int version = 1) => new()
    {
        CipherText = [1, 2, 3],
        Salt = [4, 5, 6],
        Iv = [7, 8, 9],
        Argon2Iterations = 3,
        Argon2Memory = 65536,
        Argon2Parallelism = 1,
        Version = version,
    };

    [Test]
    public async Task UploadMasterKey_FirstUpload_Succeeds()
    {
        var username = $"usermasterkey{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(MasterKeyDto()).ToUrl("/api/v1/users/master");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var user = await ctx.Users.FirstAsync(u => u.UserName == username);
        Assert.That(user.EncryptedMasterKey, Is.Not.Null);
        Assert.That(user.EncryptedMasterKey!.Version, Is.EqualTo(1));
    }

    [Test]
    public async Task UploadMasterKey_SameVersionAlreadyUploaded_ReturnsBadRequest()
    {
        var username = $"usermasterkeydup{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(MasterKeyDto(version: 2)).ToUrl("/api/v1/users/master");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(MasterKeyDto(version: 2)).ToUrl("/api/v1/users/master");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    // ── self/device-token ───────────────────────────────────────────────────

    [Test]
    public async Task CreateDeviceToken_NewToken_ReturnsCreated()
    {
        var username = $"userdevtoken{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new CreateDeviceTokenDto { Token = $"device-{Guid.NewGuid():N}" }).ToUrl("/api/v1/users/self/device-token");
            x.StatusCodeShouldBe(HttpStatusCode.Created);
        });
    }

    [Test]
    public async Task CreateDeviceToken_DuplicateToken_ReturnsAccepted()
    {
        var username = $"userdevtokendup{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);
        var deviceToken = $"device-{Guid.NewGuid():N}";

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new CreateDeviceTokenDto { Token = deviceToken }).ToUrl("/api/v1/users/self/device-token");
            x.StatusCodeShouldBe(HttpStatusCode.Created);
        });

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new CreateDeviceTokenDto { Token = deviceToken }).ToUrl("/api/v1/users/self/device-token");
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
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
