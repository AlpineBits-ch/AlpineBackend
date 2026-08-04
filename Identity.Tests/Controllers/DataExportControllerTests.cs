using System.Net;
using System.Text.Json;
using Alba;
using Identity.Application.Dtos.Request;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Tests.Controllers;

/// <summary>
/// <c>api/v1/data-exports</c> over real HTTP, real auth and real Postgres (T1-7).
/// </summary>
[TestFixture]
public class DataExportControllerTests
{
    private const string Password = "SecurePass123!";
    private const string Url = "/api/v1/data-exports";

    private static IAlbaHost Host => AppFixture.Host;

    private static async Task<(string Token, string UserId)> RegisterAsync(string prefix)
    {
        var username = $"{prefix}{Guid.NewGuid():N}"[..15];

        await Host.Scenario(x =>
        {
            x.Post.Json(new CreateUserRequest
            {
                Email = $"{username}-{Guid.NewGuid():N}@example.com",
                Password = Password,
                Username = username,
                BirthDate = DateTime.UtcNow.AddYears(-30),
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

        var token = (await tokenResult.ReadAsJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;

        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();
        var userId = await ctx.Users.Where(u => u.UserName == username).Select(u => u.Id).FirstAsync();

        return (token, userId);
    }

    private static async Task<JsonElement> RequestExportAsync(string token)
    {
        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new { }).ToUrl(Url);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        return await result.ReadAsJsonAsync<JsonElement>();
    }

    /// <summary>Fakes what the saga and the assembler would have done, so the download route can be
    /// exercised without spawning eight services.</summary>
    private static async Task MarkReadyAsync(string exportId, TimeSpan ttl)
    {
        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

        var request = await ctx.DataExportRequests.FirstAsync(r => r.Id == exportId);
        request.MarkReady(DataExportRequest.NewArtifactKey(exportId), DateTimeOffset.UtcNow, ttl);
        await ctx.SaveChangesAsync();
    }

    /// <summary>The same, for an export the fan-out could not fill in completely - what
    /// <c>AssembleUserDataExportCommandHandler</c> does when a fragment comes back with an error, or
    /// when the saga's deadline elapses and it writes a stand-in for a service that never
    /// answered.</summary>
    private static async Task MarkPartialAsync(string exportId, TimeSpan ttl, params string[] missing)
    {
        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

        var request = await ctx.DataExportRequests.FirstAsync(r => r.Id == exportId);
        request.MarkPartial(DataExportRequest.NewArtifactKey(exportId), missing, DateTimeOffset.UtcNow, ttl);
        await ctx.SaveChangesAsync();
    }

    /// <summary>Moves a request's timestamp back so the rate-limit window has passed.</summary>
    private static async Task BackdateAsync(string exportId, TimeSpan by)
    {
        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

        var request = await ctx.DataExportRequests.FirstAsync(r => r.Id == exportId);
        request.RequestedAt -= by;
        await ctx.SaveChangesAsync();
    }

    private static async Task<int> CountAuditAsync(string userId, string action)
    {
        using var scope = Host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

        return await ctx.IdentityAuditEvents.CountAsync(a => a.UserId == userId && a.Action == action);
    }

    // ── normal ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Request_Returns202PendingAndAuditsTheRequest()
    {
        var (token, userId) = await RegisterAsync("dxreq");

        var body = await RequestExportAsync(token);

        Assert.Multiple(async () =>
        {
            Assert.That(body.GetProperty("exportId").GetString(), Is.Not.Null.And.Not.Empty);
            Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("Pending"));
            Assert.That(await CountAuditAsync(userId, IdentityAuditActions.DataExportRequested), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task List_ReturnsTheCallersOwnRequests()
    {
        var (token, _) = await RegisterAsync("dxlist");
        var created = await RequestExportAsync(token);

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url(Url);
            x.StatusCodeShouldBeOk();
        });

        var rows = (await result.ReadAsJsonAsync<JsonElement>()).EnumerateArray().ToList();

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(rows[0].GetProperty("exportId").GetString(),
                Is.EqualTo(created.GetProperty("exportId").GetString()));
            Assert.That(rows[0].GetProperty("status").GetString(), Is.EqualTo("Pending"));
            Assert.That(rows[0].GetProperty("requestedAt").GetDateTimeOffset(),
                Is.LessThanOrEqualTo(DateTimeOffset.UtcNow.AddMinutes(1)));
            Assert.That(rows[0].TryGetProperty("expiresAt", out _), Is.True);
        });
    }

    [Test]
    public async Task Download_ReadyExport_Redirects302AndAuditsTheDownload()
    {
        var (token, userId) = await RegisterAsync("dxdown");
        var created = await RequestExportAsync(token);
        var exportId = created.GetProperty("exportId").GetString()!;

        await MarkReadyAsync(exportId, TimeSpan.FromDays(7));

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url($"{Url}/{exportId}/download");
            x.StatusCodeShouldBe(HttpStatusCode.Found);
        });

        Assert.Multiple(async () =>
        {
            Assert.That(result.Context.Response.Headers.Location.ToString(), Is.Not.Empty);
            // As visible as a backup read already is - see IdentityAuditActions.DataExportDownloaded.
            Assert.That(await CountAuditAsync(userId, IdentityAuditActions.DataExportDownloaded), Is.EqualTo(1));
        });
    }

    // ── edge ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Request_AfterTheWindowHasPassed_IsAllowedAgain()
    {
        var (token, _) = await RegisterAsync("dxagain");
        var first = await RequestExportAsync(token);

        await BackdateAsync(first.GetProperty("exportId").GetString()!, TimeSpan.FromHours(25));

        var second = await RequestExportAsync(token);

        Assert.That(second.GetProperty("exportId").GetString(),
            Is.Not.EqualTo(first.GetProperty("exportId").GetString()));
    }

    [Test]
    public async Task List_PartialExport_ReportsItAsPartialAndNamesTheMissingServices()
    {
        var (token, _) = await RegisterAsync("dxpart");
        var created = await RequestExportAsync(token);
        await MarkPartialAsync(created.GetProperty("exportId").GetString()!, TimeSpan.FromDays(7), "isle", "bots");

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url(Url);
            x.StatusCodeShouldBeOk();
        });

        var row = (await result.ReadAsJsonAsync<JsonElement>()).EnumerateArray().Single();
        var missing = row.GetProperty("missingServices").EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Multiple(() =>
        {
            // Not "Ready".
            Assert.That(row.GetProperty("status").GetString(), Is.EqualTo("Partial"));

            // Which services, machine-readable, so a client can render them rather than asking the
            // subject to unzip the archive and read manifest.json.
            Assert.That(missing, Is.EqualTo(new[] { "bots", "isle" }));

            Assert.That(row.GetProperty("failureReason").GetString(), Does.Contain("bots").And.Contain("isle"));
            Assert.That(row.GetProperty("expiresAt").ValueKind, Is.Not.EqualTo(JsonValueKind.Null));
        });
    }

    [Test]
    public async Task List_CompleteExport_CarriesAnEmptyMissingServicesArray()
    {
        var (token, _) = await RegisterAsync("dxcomp");
        var created = await RequestExportAsync(token);
        await MarkReadyAsync(created.GetProperty("exportId").GetString()!, TimeSpan.FromDays(7));

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url(Url);
            x.StatusCodeShouldBeOk();
        });

        var row = (await result.ReadAsJsonAsync<JsonElement>()).EnumerateArray().Single();

        Assert.Multiple(() =>
        {
            Assert.That(row.GetProperty("status").GetString(), Is.EqualTo("Ready"));
            // Never null, so a client binds to it unconditionally.
            Assert.That(row.GetProperty("missingServices").GetArrayLength(), Is.Zero);
        });
    }

    [Test]
    public async Task Request_AfterAPartialExport_IsAllowedImmediately()
    {
        var (token, _) = await RegisterAsync("dxpret");
        var first = await RequestExportAsync(token);
        await MarkPartialAsync(first.GetProperty("exportId").GetString()!, TimeSpan.FromDays(7), "messaging");

        // A partial export is an internal fault wearing the shape of an answer.
        var second = await RequestExportAsync(token);

        Assert.That(second.GetProperty("exportId").GetString(),
            Is.Not.EqualTo(first.GetProperty("exportId").GetString()));
    }

    [Test]
    public async Task Download_ExportThatIsStillPending_Is409()
    {
        var (token, _) = await RegisterAsync("dxpend");
        var created = await RequestExportAsync(token);

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url($"{Url}/{created.GetProperty("exportId").GetString()}/download");
            x.StatusCodeShouldBe(HttpStatusCode.Conflict);
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("code").GetString(), Is.EqualTo("data_export_not_ready"));
    }

    [Test]
    public async Task Download_ExportPastItsWindow_Is410EvenBeforeTheSweepHasRun()
    {
        var (token, _) = await RegisterAsync("dxgone");
        var created = await RequestExportAsync(token);
        var exportId = created.GetProperty("exportId").GetString()!;

        // Ready, with a window that closed a second ago.
        await MarkReadyAsync(exportId, TimeSpan.FromSeconds(-1));

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url($"{Url}/{exportId}/download");
            x.StatusCodeShouldBe(HttpStatusCode.Gone);
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("code").GetString(), Is.EqualTo("data_export_expired"));
    }

    // ── negative ────────────────────────────────────────────────────────────

    [Test]
    public async Task Request_TwiceInsideTheWindow_Is429WithARetryAfter()
    {
        var (token, _) = await RegisterAsync("dxrate");
        await RequestExportAsync(token);

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new { }).ToUrl(Url);
            x.StatusCodeShouldBe(HttpStatusCode.TooManyRequests);
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();

        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("code").GetString(), Is.EqualTo("data_export_rate_limited"));
            Assert.That(body.GetProperty("retryAfterSeconds").GetInt32(), Is.GreaterThan(0));
            Assert.That(result.Context.Response.Headers.RetryAfter.ToString(), Is.Not.Empty);
        });
    }

    [Test]
    public async Task Download_PartialExport_StillRedirects302AndIsAudited()
    {
        var (token, userId) = await RegisterAsync("dxpdl");
        var created = await RequestExportAsync(token);
        var exportId = created.GetProperty("exportId").GetString()!;

        await MarkPartialAsync(exportId, TimeSpan.FromDays(7), "bots", "isle");

        // The status is deliberately not Ready, and the download works anyway.
        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url($"{Url}/{exportId}/download");
            x.StatusCodeShouldBe(HttpStatusCode.Found);
        });

        Assert.Multiple(async () =>
        {
            Assert.That(result.Context.Response.Headers.Location.ToString(), Is.Not.Empty);
            Assert.That(await CountAuditAsync(userId, IdentityAuditActions.DataExportDownloaded), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Download_PartialExportPastItsWindow_Is410LikeAnyOther()
    {
        var (token, _) = await RegisterAsync("dxpgon");
        var created = await RequestExportAsync(token);
        var exportId = created.GetProperty("exportId").GetString()!;

        // Partial buys the archive no extra life: same seven-day clock, same 410.
        await MarkPartialAsync(exportId, TimeSpan.FromSeconds(-1), "bots");

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url($"{Url}/{exportId}/download");
            x.StatusCodeShouldBe(HttpStatusCode.Gone);
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("code").GetString(), Is.EqualTo("data_export_expired"));
    }

    [Test]
    public async Task Download_SomebodyElsesExport_Is404AndWritesNoAuditRow()
    {
        var (ownerToken, _) = await RegisterAsync("dxowner");
        var (intruderToken, intruderId) = await RegisterAsync("dxintr");

        var created = await RequestExportAsync(ownerToken);
        var exportId = created.GetProperty("exportId").GetString()!;
        await MarkReadyAsync(exportId, TimeSpan.FromDays(7));

        // 404, not 403: a distinguishable refusal would confirm the export id exists, which is an
        // oracle over other people's requests for their own data.
        await Host.Scenario(x =>
        {
            x.WithBearerToken(intruderToken);
            x.Get.Url($"{Url}/{exportId}/download");
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });

        Assert.That(await CountAuditAsync(intruderId, IdentityAuditActions.DataExportDownloaded), Is.Zero);
    }

    [Test]
    public async Task List_WithoutAToken_Is401()
    {
        await Host.Scenario(x =>
        {
            x.Get.Url(Url);
            x.StatusCodeShouldBe(HttpStatusCode.Unauthorized);
        });
    }

    [Test]
    public async Task Download_UnknownExportId_Is404()
    {
        var (token, _) = await RegisterAsync("dxunk");

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url($"{Url}/dxrq_DOESNOTEXIST/download");
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }

    [Test]
    public async Task List_DoesNotExposeTheArtifactKey()
    {
        var (token, _) = await RegisterAsync("dxkey");
        var created = await RequestExportAsync(token);
        await MarkReadyAsync(created.GetProperty("exportId").GetString()!, TimeSpan.FromDays(7));

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Get.Url(Url);
            x.StatusCodeShouldBeOk();
        });

        var raw = await result.ReadAsTextAsync();

        // The key is a bearer credential for the archive.
        Assert.That(raw, Does.Not.Contain("data-exports/"));
        Assert.That(raw, Does.Not.Contain("artifactKey"));
    }
}
