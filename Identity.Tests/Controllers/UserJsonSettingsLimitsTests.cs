using System.Net;
using System.Text.Json;
using Alba;
using Identity.Application.Controllers;
using Identity.Application.Dtos.Request;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Tests.Controllers;

/// <summary>
/// T0-6: the limits on <c>PUT api/v1/users/self/settings</c>, plus T0-1's additive
/// <c>privacySettings</c> key on <c>GET api/v1/users/self</c>.
///
/// <para><c>JsonSettings</c> used to accept an arbitrary <c>JsonElement</c> of any size, shape and
/// depth and store it forever on a row that every self-payload read loads. The tests that matter
/// here are the refusals - and, in each of them, that the previously stored document is still
/// intact afterwards.</para>
/// </summary>
[TestFixture]
public class UserJsonSettingsLimitsTests
{
    private const string Password = "SecurePass123!";
    private const string SettingsUrl = "/api/v1/users/self/settings";

    private static IAlbaHost Host => AppFixture.Host;

    private static async Task<string> RegisterAndLoginAsync(string prefix)
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
        return body.GetProperty("access_token").GetString()!;
    }

    private static Task PutSettingsAsync(string token, string json, HttpStatusCode expected) =>
        Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.RawJson(json);
            x.Put.Url(SettingsUrl);
            x.StatusCodeShouldBe(expected);
        });

    private static async Task<JsonElement> GetSettingsAsync(string token)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url(SettingsUrl);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        return await result.ReadAsJsonAsync<JsonElement>();
    }

    // ── normal ──────────────────────────────────────────────────────────────

    [Test]
    public async Task PutSettings_OrdinaryDocument_IsStored()
    {
        var token = await RegisterAndLoginAsync("jsok");

        await PutSettingsAsync(token, """{"sidebar":{"width":240,"collapsed":false},"lastChannel":"chan_1"}""",
            HttpStatusCode.OK);

        var body = await GetSettingsAsync(token);
        Assert.That(body.GetProperty("sidebar").GetProperty("width").GetInt32(), Is.EqualTo(240));
    }

    // ── edge ────────────────────────────────────────────────────────────────

    [Test]
    public async Task PutSettings_JustUnderTheSizeCap_IsAccepted()
    {
        var token = await RegisterAndLoginAsync("jsedge");

        // {"pad":"aaa...aaa"} - sized so the serialized document lands a few bytes under the cap.
        var padding = new string('a', UserController.MaxJsonSettingsBytes - 20);
        await PutSettingsAsync(token, $$"""{"pad":"{{padding}}"}""", HttpStatusCode.OK);
    }

    [Test]
    public async Task PutSettings_AtTheDepthCap_IsAccepted()
    {
        var token = await RegisterAndLoginAsync("jsdepth");

        await PutSettingsAsync(token, NestedObject(UserController.MaxJsonSettingsDepth), HttpStatusCode.OK);
    }

    // ── negative ────────────────────────────────────────────────────────────

    [Test]
    public async Task PutSettings_OverTheSizeCap_ReturnsPayloadTooLargeAndKeepsTheOldDocument()
    {
        var token = await RegisterAndLoginAsync("jsbig");
        await PutSettingsAsync(token, """{"keep":"me"}""", HttpStatusCode.OK);

        var padding = new string('a', UserController.MaxJsonSettingsBytes + 100);
        await PutSettingsAsync(token, $$"""{"pad":"{{padding}}"}""", HttpStatusCode.RequestEntityTooLarge);

        var body = await GetSettingsAsync(token);
        Assert.That(body.GetProperty("keep").GetString(), Is.EqualTo("me"),
            "a refused write must not have replaced what was already stored");
    }

    [TestCase("[]")]
    [TestCase("[1,2,3]")]
    [TestCase("\"just a string\"")]
    [TestCase("42")]
    [TestCase("true")]
    [TestCase("null")]
    public async Task PutSettings_NonObjectRoot_ReturnsBadRequest(string json)
    {
        // Every client merges keys into this document. A root that is not an object breaks all of
        // them, and does it on the next read rather than on the write that caused it.
        var token = await RegisterAndLoginAsync("jsroot");

        await PutSettingsAsync(token, json, HttpStatusCode.BadRequest);

        var body = await GetSettingsAsync(token);
        Assert.That(body.ValueKind, Is.EqualTo(JsonValueKind.Object));
    }

    [Test]
    public async Task PutSettings_TooDeeplyNested_ReturnsBadRequest()
    {
        var token = await RegisterAndLoginAsync("jsdeep");
        await PutSettingsAsync(token, """{"keep":"me"}""", HttpStatusCode.OK);

        await PutSettingsAsync(token, NestedObject(UserController.MaxJsonSettingsDepth + 1),
            HttpStatusCode.BadRequest);

        var body = await GetSettingsAsync(token);
        Assert.That(body.GetProperty("keep").GetString(), Is.EqualTo("me"));
    }

    [Test]
    public async Task PutSettings_DeepNestingThroughArrays_IsAlsoRefused()
    {
        // Arrays count towards depth too - otherwise the cap is trivially sidestepped by wrapping
        // every level in [ ] instead of { }.
        var token = await RegisterAndLoginAsync("jsdeeparr");

        var deep = "{\"a\":" + new string('[', 40) + new string(']', 40) + "}";
        await PutSettingsAsync(token, deep, HttpStatusCode.BadRequest);
    }

    // ── JsonDepth itself ────────────────────────────────────────────────────

    [Test]
    public void JsonDepth_CountsTheRootAsLevelOne()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Depth("{}"), Is.EqualTo(1));
            Assert.That(Depth("""{"a":1}"""), Is.EqualTo(2));
            Assert.That(Depth("""{"a":{"b":1}}"""), Is.EqualTo(3));
            Assert.That(Depth("""{"a":[{"b":1}]}"""), Is.EqualTo(4));
            // The deepest branch wins, not the last one walked.
            Assert.That(Depth("""{"a":{"b":{"c":1}},"d":1}"""), Is.EqualTo(4));
        });
    }

    private static int Depth(string json) =>
        UserController.JsonDepth(JsonSerializer.Deserialize<JsonElement>(json));

    /// <summary>An object nested to exactly <paramref name="depth"/> levels, counting the root.</summary>
    private static string NestedObject(int depth)
    {
        var json = "1";
        for (var i = 0; i < depth - 1; i++) json = $$"""{"a":{{json}}}""";
        return json;
    }

    // ── T0-1: GET /users/self stays additive ────────────────────────────────

    [Test]
    public async Task GetSelf_ExposesPrivacySettingsWithoutDisturbingUserPreferences()
    {
        var token = await RegisterAndLoginAsync("selfpv");

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.RawJson("""{"hidePushContent": true, "directMessagePolicy": "Nobody"}""");
            x.Patch.Url("/api/v1/privacy-settings");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url("/api/v1/users/self");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();

        Assert.That(body.TryGetProperty("privacySettings", out var privacy), Is.True,
            "T0-1 puts the enforced settings on the self payload under their own key");
        Assert.Multiple(() =>
        {
            Assert.That(privacy.GetProperty("hidePushContent").GetBoolean(), Is.True);
            Assert.That(privacy.GetProperty("directMessagePolicy").GetString(), Is.EqualTo("Nobody"));
            Assert.That(privacy.GetProperty("version").GetInt32(), Is.EqualTo(1));
        });

        // The legacy block is untouched by all of this - a v1 client keeps parsing what it always
        // parsed, which is the whole point of the additive rule.
        Assert.That(body.TryGetProperty("userPreferences", out var preferences), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(preferences.TryGetProperty("privacySettings", out _), Is.True,
                "the legacy flags enum must still be there");
            Assert.That(preferences.TryGetProperty("directMessageSettings", out var legacyDm), Is.True);
            Assert.That(legacyDm.GetString(), Is.EqualTo("FilterNonFriends"),
                "the new endpoint writes the new record only; the legacy column is frozen, not mirrored");
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
