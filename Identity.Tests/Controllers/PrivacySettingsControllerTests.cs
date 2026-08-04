using System.Net;
using System.Text.Json;
using Alba;
using Identity.Application.Dtos.Request;
using Identity.Domain.Entities;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Tests.Controllers;

/// <summary>
/// <c>GET</c>/<c>PATCH api/v1/privacy-settings</c> over real HTTP, real auth and real Postgres.
/// </summary>
[TestFixture]
public class PrivacySettingsControllerTests
{
    private const string Password = "SecurePass123!";
    private const string Url = "/api/v1/privacy-settings";

    private static IAlbaHost Host => AppFixture.Host;

    private static async Task<(string Token, string Username)> RegisterAndLoginAsync(string prefix)
    {
        var username = $"{prefix}{Guid.NewGuid():N}"[..15];

        await Host.Scenario(x =>
        {
            x.Post.Json(new CreateUserRequest
            {
                Email = $"{username}-{Guid.NewGuid():N}@example.com",
                Password = Password,
                Username = username,
                BirthDate = DateTime.UtcNow.AddYears(-25),
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
        return (body.GetProperty("access_token").GetString()!, username);
    }

    private static async Task<JsonElement> GetAsync(string token)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url(Url);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        return await result.ReadAsJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> PatchAsync(string token, string json, HttpStatusCode expected)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.RawJson(json);
            x.Patch.Url(Url);
            x.StatusCodeShouldBe(expected);
        });
        return expected == HttpStatusCode.OK ? await result.ReadAsJsonAsync<JsonElement>() : default;
    }

    // ── GET ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task Get_NewAccount_ReturnsTheDocumentedDefaults()
    {
        var (token, _) = await RegisterAndLoginAsync("pvget");

        var body = await GetAsync(token);

        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("allowDataCollection").GetBoolean(), Is.False);
            Assert.That(body.GetProperty("allowPersonalization").GetBoolean(), Is.False);
            Assert.That(body.GetProperty("allowVoiceRecordingInClips").GetBoolean(), Is.False);
            Assert.That(body.GetProperty("directMessagePolicy").GetString(), Is.EqualTo("Friends"));
            Assert.That(body.GetProperty("friendRequestPolicy").GetString(), Is.EqualTo("Everyone"));
            Assert.That(body.GetProperty("discoverableByUsername").GetBoolean(), Is.True);
            Assert.That(body.GetProperty("discoverableByEmail").GetBoolean(), Is.False);
            Assert.That(body.GetProperty("birthdayVisibility").GetString(), Is.EqualTo("Nobody"));
            Assert.That(body.GetProperty("explicitContentFilter").GetString(), Is.EqualTo("UnknownSenders"));
            Assert.That(body.GetProperty("dmRetentionDays").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(body.GetProperty("version").GetInt32(), Is.Zero);
        });
    }

    [Test]
    public async Task Get_WithoutBearerToken_ReturnsUnauthorized()
    {
        // The record names who may contact this account and what it consented to. It is not public.
        await Host.Scenario(x =>
        {
            x.Get.Url(Url);
            x.StatusCodeShouldBe(HttpStatusCode.Unauthorized);
        });
    }

    [Test]
    public async Task Patch_WithoutBearerToken_ReturnsUnauthorized()
    {
        await Host.Scenario(x =>
        {
            x.RawJson("""{"hidePushContent":true}""");
            x.Patch.Url(Url);
            x.StatusCodeShouldBe(HttpStatusCode.Unauthorized);
        });
    }

    // ── PATCH: normal ───────────────────────────────────────────────────────

    [Test]
    public async Task Patch_RoundTripsEveryField()
    {
        var (token, _) = await RegisterAndLoginAsync("pvall");

        var written = await PatchAsync(token, """
            {
              "allowDataCollection": true,
              "allowPersonalization": true,
              "allowVoiceRecordingInClips": true,
              "directMessagePolicy": "Everyone",
              "friendRequestPolicy": "Nobody",
              "discoverableByUsername": false,
              "discoverableByEmail": true,
              "discoverableByPhone": true,
              "mutualServersVisibility": "Everyone",
              "mutualFriendsVisibility": "Nobody",
              "connectionsVisibility": "Everyone",
              "birthdayVisibility": "Friends",
              "shareActivity": false,
              "allowPositionalVoiceCapture": false,
              "sendReadReceipts": false,
              "sendTypingIndicators": false,
              "dmRetentionDays": 45,
              "explicitContentFilter": "Off",
              "hidePushContent": true
            }
            """, HttpStatusCode.OK);

        var reread = await GetAsync(token);

        foreach (var body in new[] { written, reread })
        {
            Assert.Multiple(() =>
            {
                Assert.That(body.GetProperty("allowDataCollection").GetBoolean(), Is.True);
                Assert.That(body.GetProperty("allowPersonalization").GetBoolean(), Is.True);
                Assert.That(body.GetProperty("allowVoiceRecordingInClips").GetBoolean(), Is.True);
                Assert.That(body.GetProperty("directMessagePolicy").GetString(), Is.EqualTo("Everyone"));
                Assert.That(body.GetProperty("friendRequestPolicy").GetString(), Is.EqualTo("Nobody"));
                Assert.That(body.GetProperty("discoverableByUsername").GetBoolean(), Is.False);
                Assert.That(body.GetProperty("discoverableByEmail").GetBoolean(), Is.True);
                Assert.That(body.GetProperty("discoverableByPhone").GetBoolean(), Is.True);
                Assert.That(body.GetProperty("mutualServersVisibility").GetString(), Is.EqualTo("Everyone"));
                Assert.That(body.GetProperty("mutualFriendsVisibility").GetString(), Is.EqualTo("Nobody"));
                Assert.That(body.GetProperty("connectionsVisibility").GetString(), Is.EqualTo("Everyone"));
                Assert.That(body.GetProperty("birthdayVisibility").GetString(), Is.EqualTo("Friends"));
                Assert.That(body.GetProperty("shareActivity").GetBoolean(), Is.False);
                Assert.That(body.GetProperty("allowPositionalVoiceCapture").GetBoolean(), Is.False);
                Assert.That(body.GetProperty("sendReadReceipts").GetBoolean(), Is.False);
                Assert.That(body.GetProperty("sendTypingIndicators").GetBoolean(), Is.False);
                Assert.That(body.GetProperty("dmRetentionDays").GetInt32(), Is.EqualTo(45));
                Assert.That(body.GetProperty("explicitContentFilter").GetString(), Is.EqualTo("Off"));
                Assert.That(body.GetProperty("hidePushContent").GetBoolean(), Is.True);
            });
        }
    }

    [Test]
    public async Task Patch_PartialWrite_LeavesOmittedFieldsAlone()
    {
        var (token, _) = await RegisterAndLoginAsync("pvpart");

        await PatchAsync(token, """{"hidePushContent": true, "dmRetentionDays": 7}""", HttpStatusCode.OK);
        var body = await PatchAsync(token, """{"shareActivity": false}""", HttpStatusCode.OK);

        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("shareActivity").GetBoolean(), Is.False);
            Assert.That(body.GetProperty("hidePushContent").GetBoolean(), Is.True);
            Assert.That(body.GetProperty("dmRetentionDays").GetInt32(), Is.EqualTo(7));
        });
    }

    [Test]
    public async Task Patch_BumpsVersionAndWritesAnAuditRowNamingTheChangedFieldsOnly()
    {
        var (token, username) = await RegisterAndLoginAsync("pvaudit");

        var body = await PatchAsync(token, """{"discoverableByEmail": true}""", HttpStatusCode.OK);
        Assert.That(body.GetProperty("version").GetInt32(), Is.EqualTo(1));

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var user = await ctx.Users.FirstAsync(u => u.UserName == username);

        var audit = await ctx.IdentityAuditEvents
            .Where(a => a.UserId == user.Id && a.Action == IdentityAuditActions.PrivacySettingsChanged)
            .ToListAsync();

        Assert.That(audit, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(audit[0].Detail, Does.Contain("discoverableByEmail"));
            Assert.That(audit[0].Detail, Does.Not.Contain("true"),
                "the audit records which control changed, never the value - the row is append-only "
                + "and never purged, and a second copy of the user's settings in it is a second "
                + "thing to leak");
        });
    }

    [Test]
    public async Task Patch_TwoWrites_BumpVersionMonotonically()
    {
        var (token, _) = await RegisterAndLoginAsync("pvver");

        var first = await PatchAsync(token, """{"hidePushContent": true}""", HttpStatusCode.OK);
        var second = await PatchAsync(token, """{"shareActivity": false}""", HttpStatusCode.OK);

        Assert.That(first.GetProperty("version").GetInt32(), Is.EqualTo(1));
        Assert.That(second.GetProperty("version").GetInt32(), Is.EqualTo(2));
    }

    // ── PATCH: edge ─────────────────────────────────────────────────────────

    [Test]
    public async Task Patch_EmptyObject_ChangesNothingAndReturnsCurrentState()
    {
        var (token, username) = await RegisterAndLoginAsync("pvnoop");

        await PatchAsync(token, """{"hidePushContent": true}""", HttpStatusCode.OK);
        var body = await PatchAsync(token, "{}", HttpStatusCode.OK);

        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("hidePushContent").GetBoolean(), Is.True);
            Assert.That(body.GetProperty("version").GetInt32(), Is.EqualTo(1),
                "an empty patch is not a write, so it must not bump the version");
        });

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var user = await ctx.Users.FirstAsync(u => u.UserName == username);
        var auditCount = await ctx.IdentityAuditEvents
            .CountAsync(a => a.UserId == user.Id && a.Action == IdentityAuditActions.PrivacySettingsChanged);

        Assert.That(auditCount, Is.EqualTo(1), "the no-op patch must not have left a second audit row");
    }

    [Test]
    public async Task Patch_RewritingTheCurrentState_DoesNotBumpTheVersion()
    {
        var (token, _) = await RegisterAndLoginAsync("pvsame");

        var body = await PatchAsync(token, """{"directMessagePolicy": "Friends"}""", HttpStatusCode.OK);

        Assert.That(body.GetProperty("version").GetInt32(), Is.Zero,
            "every consuming service treats the change event as 'drop your cache'; an idle client "
            + "re-posting what it read must not trigger a fleet-wide re-read");
    }

    [Test]
    public async Task Patch_ClearingTheRetentionWindow_IsDistinctFromOmittingIt()
    {
        var (token, _) = await RegisterAndLoginAsync("pvret");

        await PatchAsync(token, """{"dmRetentionDays": 30}""", HttpStatusCode.OK);
        var omitted = await PatchAsync(token, """{"shareActivity": false}""", HttpStatusCode.OK);
        Assert.That(omitted.GetProperty("dmRetentionDays").GetInt32(), Is.EqualTo(30));

        var cleared = await PatchAsync(token, """{"dmRetentionDays": null}""", HttpStatusCode.OK);
        Assert.That(cleared.GetProperty("dmRetentionDays").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }

    // ── PATCH: negative ─────────────────────────────────────────────────────

    [Test]
    public async Task Patch_UnknownField_ReturnsBadRequest()
    {
        var (token, _) = await RegisterAndLoginAsync("pvunk");

        await PatchAsync(token, """{"allowEverything": true}""", HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Patch_UnknownFieldBesideAValidOne_AppliesNeitherAndDoesNotBumpVersion()
    {
        var (token, _) = await RegisterAndLoginAsync("pvmix");

        await PatchAsync(token, """{"hidePushContent": true, "hidePushContnet": true}""",
            HttpStatusCode.BadRequest);

        var body = await GetAsync(token);
        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("hidePushContent").GetBoolean(), Is.False,
                "a refused patch must not have applied the half of it that parsed");
            Assert.That(body.GetProperty("version").GetInt32(), Is.Zero);
        });
    }

    [TestCase("""{"directMessagePolicy": "Anyone"}""")]
    [TestCase("""{"friendRequestPolicy": 99}""")]
    [TestCase("""{"hidePushContent": "yes"}""")]
    [TestCase("""{"allowDataCollection": null}""")]
    [TestCase("""{"dmRetentionDays": 0}""")]
    [TestCase("""{"dmRetentionDays": 99999}""")]
    [TestCase("[]")]
    [TestCase("\"nope\"")]
    public async Task Patch_MalformedBody_ReturnsBadRequest(string json)
    {
        var (token, _) = await RegisterAndLoginAsync("pvbad");

        await PatchAsync(token, json, HttpStatusCode.BadRequest);

        var body = await GetAsync(token);
        Assert.That(body.GetProperty("version").GetInt32(), Is.Zero,
            "a refused write leaves no trace on the record");
    }

    [Test]
    public async Task Patch_CannotSetVersion()
    {
        // Version is the server's monotonic counter.
        var (token, _) = await RegisterAndLoginAsync("pvversw");

        await PatchAsync(token, """{"version": 99}""", HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Patch_CannotWriteAnotherAccountsRecord()
    {
        // There is no route that takes a user id, and there must not be one by accident: userId is
        // read from the token, so a body carrying one is an unknown field.
        var (token, _) = await RegisterAndLoginAsync("pvother");

        await PatchAsync(token, """{"userId": "user_someoneelse", "hidePushContent": true}""",
            HttpStatusCode.BadRequest);
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
