using System.Net;
using System.Text.Json;
using Alba;
using Domain;
using Identity.Application.Dtos.Request;
using Identity.Application.Services;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Tests.Controllers;

/// <summary>T1-11 over real HTTP: the minor protections as a client experiences them.</summary>
[TestFixture]
public class PrivacySettingsMinorTests
{
    private const string Password = "SecurePass123!";
    private const string Url = "/api/v1/privacy-settings";

    private static IAlbaHost Host => AppFixture.Host;

    private static async Task<(string Token, string Username)> RegisterAndLoginAsync(string prefix, int ageYears)
    {
        var username = $"{prefix}{Guid.NewGuid():N}"[..15];

        await Host.Scenario(x =>
        {
            x.Post.Json(new CreateUserRequest
            {
                Email = $"{username}-{Guid.NewGuid():N}@example.com",
                Password = Password,
                Username = username,
                // Two days past the birthday, so a test run on any day of the year lands the account
                // squarely on the intended side of the boundary.
                BirthDate = DateTime.UtcNow.AddYears(-ageYears).AddDays(-2),
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

    private static async Task<JsonElement> PatchAsync(string token, string json, HttpStatusCode expected)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.RawJson(json);
            x.Patch.Url(Url);
            x.StatusCodeShouldBe(expected);
        });
        return await result.ReadAsJsonAsync<JsonElement>();
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

    // ── negative: the refusals ──────────────────────────────────────────────

    [TestCase("""{"directMessagePolicy":"Everyone"}""", "directMessagePolicy")]
    [TestCase("""{"allowPersonalization":true}""", "allowPersonalization")]
    [TestCase("""{"discoverableByEmail":true}""", "discoverableByEmail")]
    [TestCase("""{"discoverableByPhone":true}""", "discoverableByPhone")]
    [TestCase("""{"allowVoiceRecordingInClips":true}""", "allowVoiceRecordingInClips")]
    [TestCase("""{"explicitContentFilter":"Off"}""", "explicitContentFilter")]
    public async Task Patch_AsAMinor_Returns403MinorRestrictionNamingTheField(string json, string field)
    {
        var (token, _) = await RegisterAndLoginAsync("pvmin", ageYears: 15);

        var body = await PatchAsync(token, json, HttpStatusCode.Forbidden);

        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("code").GetString(), Is.EqualTo(MinorPrivacyFloors.RestrictionCode));
            Assert.That(body.GetProperty("field").GetString(), Is.EqualTo(field));
        });

        var settings = await GetAsync(token);
        Assert.That(settings.GetProperty("version").GetInt32(), Is.Zero,
            "a refused write leaves no trace on the record");
    }

    [Test]
    public async Task Patch_AsAMinor_MixingAPermittedFieldWithARefusedOne_AppliesNeither()
    {
        var (token, _) = await RegisterAndLoginAsync("pvminmix", ageYears: 15);

        await PatchAsync(token, """{"hidePushContent":true,"allowPersonalization":true}""",
            HttpStatusCode.Forbidden);

        var settings = await GetAsync(token);
        Assert.Multiple(() =>
        {
            Assert.That(settings.GetProperty("hidePushContent").GetBoolean(), Is.False);
            Assert.That(settings.GetProperty("version").GetInt32(), Is.Zero);
        });
    }

    // ── normal: an adult is unaffected ──────────────────────────────────────

    [Test]
    public async Task Patch_AsAnAdult_AcceptsEveryOneOfTheRestrictedValues()
    {
        var (token, _) = await RegisterAndLoginAsync("pvadult", ageYears: 25);

        var body = await PatchAsync(token, """
            {
              "directMessagePolicy": "Everyone",
              "allowPersonalization": true,
              "discoverableByEmail": true,
              "discoverableByPhone": true,
              "allowVoiceRecordingInClips": true,
              "explicitContentFilter": "Off"
            }
            """, HttpStatusCode.OK);

        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("directMessagePolicy").GetString(), Is.EqualTo("Everyone"));
            Assert.That(body.GetProperty("allowPersonalization").GetBoolean(), Is.True);
            Assert.That(body.GetProperty("explicitContentFilter").GetString(), Is.EqualTo("Off"));
        });
    }

    // ── edge ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Patch_AsAMinor_StillAcceptsUnrestrictedFields()
    {
        // The floors are six specific fields, not a blanket read-only account.
        var (token, _) = await RegisterAndLoginAsync("pvminok", ageYears: 15);

        var body = await PatchAsync(token,
            """{"hidePushContent":true,"sendTypingIndicators":false,"birthdayVisibility":"Friends"}""",
            HttpStatusCode.OK);

        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("hidePushContent").GetBoolean(), Is.True);
            Assert.That(body.GetProperty("sendTypingIndicators").GetBoolean(), Is.False);
            Assert.That(body.GetProperty("birthdayVisibility").GetString(), Is.EqualTo("Friends"));
        });
    }

    [Test]
    public async Task Get_AsAMinor_ReportsTheFlooredValuesEvenWhenTheStoredRowIsWider()
    {
        // The row is widened directly in the database - simulating a value written before the
        // account's birth date said what it says now, or by a path that predates the floors.
        var (token, username) = await RegisterAndLoginAsync("pvminclamp", ageYears: 15);

        using (var scope = Host.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
            var user = await ctx.Users.FirstAsync(u => u.UserName == username);
            var settings = await ctx.UserPrivacySettings.FirstAsync(p => p.UserId == user.Id);
            settings.DirectMessagePolicy = DirectMessagePolicy.Everyone;
            settings.AllowPersonalization = true;
            settings.ExplicitContentFilter = ExplicitContentFilter.Off;
            await ctx.SaveChangesAsync();
        }

        var reported = await GetAsync(token);

        Assert.Multiple(() =>
        {
            Assert.That(reported.GetProperty("directMessagePolicy").GetString(), Is.EqualTo("Friends"));
            Assert.That(reported.GetProperty("allowPersonalization").GetBoolean(), Is.False);
            Assert.That(reported.GetProperty("explicitContentFilter").GetString(), Is.EqualTo("UnknownSenders"));
        });
    }

    [Test]
    public async Task AgingOut_UnlocksTheSettingsRatherThanLeavingThemRestricted()
    {
        // The birthday rollover, simulated by moving the birth date rather than the clock: what the
        // endpoint derives on every request is what changes, and the stored choices come back
        // untouched because the clamp never wrote through to them.
        var (token, username) = await RegisterAndLoginAsync("pvminage", ageYears: 15);

        await PatchAsync(token, """{"directMessagePolicy":"Everyone"}""", HttpStatusCode.Forbidden);

        using (var scope = Host.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
            var user = await ctx.Users.FirstAsync(u => u.UserName == username);
            user.AgeVerification.BirthDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-19));
            await ctx.SaveChangesAsync();
        }

        var body = await PatchAsync(token, """{"directMessagePolicy":"Everyone"}""", HttpStatusCode.OK);

        Assert.That(body.GetProperty("directMessagePolicy").GetString(), Is.EqualTo("Everyone"),
            "a user who ages out gets the settings unlocked, not silently kept restricted");
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
