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
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
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
    public async Task UploadMasterKey_SameVersionSameBytes_IsIdempotent()
    {
        // The old guard refused this - it compared versions and rejected on a *match*, which meant a
        // client retrying a request whose response it never saw was told its upload had failed.
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
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
    }

    [Test]
    public async Task UploadMasterKey_SameVersionDifferentBytes_WithoutPassword_ReturnsBadRequest()
    {
        // This is the case the old guard let through: same version number, *different* wrapped key,
        // no re-auth and no audit. It silently replaces the envelope that makes every backup blob
        // readable - so the write is refused without the account password.
        var username = $"usermasterkeysub{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(MasterKeyDto(version: 2)).ToUrl("/api/v1/users/master");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var substituted = MasterKeyDto(version: 2);
        substituted.CipherText = [9, 9, 9];

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(substituted).ToUrl("/api/v1/users/master");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var user = await ctx.Users.FirstAsync(u => u.UserName == username);
        Assert.That(user.EncryptedMasterKey!.CipherText, Is.EqualTo(new byte[] { 1, 2, 3 }),
            "The stored envelope must be untouched by a refused replacement");
    }

    [Test]
    public async Task UploadMasterKey_NewVersion_WithoutPassword_ReturnsBadRequest()
    {
        var username = $"usermasterkeyver{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(MasterKeyDto(version: 1)).ToUrl("/api/v1/users/master");
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

    // ── self/onboarding ─────────────────────────────────────────────────────

    private static async Task<JsonElement> PutOnboardingAsync(string token, object body, HttpStatusCode expected)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Put.Json(body).ToUrl("/api/v1/users/self/onboarding");
            x.StatusCodeShouldBe(expected);
        });
        return expected == HttpStatusCode.OK ? await result.ReadAsJsonAsync<JsonElement>() : default;
    }

    private static string[] InterestNames(JsonElement body) =>
        body.GetProperty("interests").EnumerateArray().Select(e => e.GetString()!).ToArray();

    [Test]
    public async Task UpdateOnboarding_FirstAnswer_StampsOnboardedAtAndInterests()
    {
        var username = $"onbfirst{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);

        var body = await PutOnboardingAsync(token, new { interests = new[] { "isle" } }, HttpStatusCode.OK);
        Assert.That(InterestNames(body), Is.EqualTo(new[] { "isle" }));

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var user = await ctx.Users.FirstAsync(u => u.UserName == username);
        Assert.That(user.OnboardedAt, Is.Not.Null);
        Assert.That(user.Interests, Is.EqualTo(UserInterests.Isle));
    }

    /// <summary>
    /// Re-running the picker from settings has to be able to change the answer. This is the path an
    /// Isle-only account takes when it later completes key setup and reports that it now wants the
    /// messaging half too.
    /// </summary>
    [Test]
    public async Task UpdateOnboarding_SecondAnswer_ChangesInterestsButKeepsOriginalTimestamp()
    {
        var username = $"onbsecond{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);

        await PutOnboardingAsync(token, new { interests = new[] { "isle" } }, HttpStatusCode.OK);

        DateTimeOffset? firstStamp;
        using (var scope = Host.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
            firstStamp = (await ctx.Users.FirstAsync(u => u.UserName == username)).OnboardedAt;
        }

        var body = await PutOnboardingAsync(token, new { interests = new[] { "isle", "social" } }, HttpStatusCode.OK);
        Assert.That(InterestNames(body), Is.EqualTo(new[] { "isle", "social" }));

        using var verifyScope = Host.Services.CreateScope();
        var verifyCtx = verifyScope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var user = await verifyCtx.Users.FirstAsync(u => u.UserName == username);
        Assert.That(user.Interests, Is.EqualTo(UserInterests.Isle | UserInterests.Social));
        Assert.That(user.OnboardedAt, Is.EqualTo(firstStamp),
            "re-answering must not rewrite when the account was first onboarded");
    }

    /// <summary>
    /// No interest at all is a state the client's launch sequence has no answer for - it cannot
    /// decide whether a master key is owed - so it must be unreachable rather than merely unusual.
    /// </summary>
    [Test]
    public async Task UpdateOnboarding_EmptyOrUnknownInterests_ReturnsBadRequest()
    {
        var username = $"onbempty{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);

        await PutOnboardingAsync(token, new { interests = Array.Empty<string>() }, HttpStatusCode.BadRequest);
        await PutOnboardingAsync(token, new { interests = (string[]?)null }, HttpStatusCode.BadRequest);
        await PutOnboardingAsync(token, new { interests = new[] { "dinosaurs" } }, HttpStatusCode.BadRequest);
        // A partly-known set is refused whole: storing the half we understood would leave the
        // account believing it chose something it did not.
        await PutOnboardingAsync(token, new { interests = new[] { "isle", "dinosaurs" } }, HttpStatusCode.BadRequest);

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var user = await ctx.Users.FirstAsync(u => u.UserName == username);
        Assert.That(user.OnboardedAt, Is.Null, "a refused write must not stamp the account");
        Assert.That(user.Interests, Is.EqualTo(UserInterests.None));
    }

    /// <summary>
    /// The client reads both fields off the self payload rather than a dedicated route, so they have
    /// to survive the facet - Interests in particular is excluded from it and reshaped by hand into
    /// a name array, which is exactly the kind of wiring that silently returns empty.
    /// </summary>
    [Test]
    public async Task GetSelf_AfterOnboarding_ExposesOnboardedAtAndInterests()
    {
        var username = $"onbself{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);
        await PutOnboardingAsync(token, new { interests = new[] { "isle", "social" } }, HttpStatusCode.OK);

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url("/api/v1/users/self");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("onboardedAt").GetString(), Is.Not.Null.And.Not.Empty);
        Assert.That(InterestNames(body), Is.EqualTo(new[] { "isle", "social" }));
    }

    /// <summary>
    /// A fresh account must report a null timestamp, because that null is the only thing telling the
    /// client to show the picker at all. The field being <i>present</i> matters as much as its
    /// value: the client reads an absent field as "already onboarded", so that older servers do not
    /// wall their users behind a picker whose submit route they do not serve.
    /// </summary>
    [Test]
    public async Task GetSelf_BeforeOnboarding_ReportsNullOnboardedAtAndNoInterests()
    {
        var username = $"onbfresh{Guid.NewGuid():N}"[..15];
        var token = await RegisterAndLoginAsync(username);

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url("/api/v1/users/self");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();
        Assert.That(body.TryGetProperty("onboardedAt", out var stamp), Is.True);
        Assert.That(stamp.ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(InterestNames(body), Is.Empty);
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
