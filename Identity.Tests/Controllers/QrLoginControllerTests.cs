using System.Net;
using System.Text.Json;
using Alba;
using Identity.Application.Dtos.Request;
using Identity.Application.Services.Qr;
using Identity.Domain.Enums;
using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Tests.Controllers;

/// <summary>
/// Covers the QR pairing handshake (start/status/scan/approve) end to end via HTTP. Token
/// issuance itself (the /connect/token qr_login grant) is covered separately in
/// ConnectControllerTests - this file only exercises the pre-auth state machine.
/// </summary>
[TestFixture]
public class QrLoginControllerTests
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

    private static async Task<string> StartPairingAsync(string deviceName = "Test Desktop", DeviceType deviceType = DeviceType.Desktop)
    {
        var result = await Host.Scenario(x =>
        {
            x.Post.Json(new StartQrLoginDto { DeviceName = deviceName, DeviceType = deviceType }).ToUrl("/api/v1/qr-login/start");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        var body = await result.ReadAsJsonAsync<JsonElement>();
        return body.GetProperty("code").GetString()!;
    }

    // ── start ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Start_ReturnsCodeAndExpiry()
    {
        var result = await Host.Scenario(x =>
        {
            x.Post.Json(new StartQrLoginDto { DeviceName = "Chrome on Windows", DeviceType = DeviceType.Web }).ToUrl("/api/v1/qr-login/start");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("code").GetString(), Is.Not.Null.And.Not.Empty);
        Assert.That(body.GetProperty("expiresInSeconds").GetInt32(), Is.EqualTo(180));
    }

    [Test]
    public async Task Start_MissingDeviceName_ReturnsBadRequest()
    {
        await Host.Scenario(x =>
        {
            x.Post.Json(new StartQrLoginDto { DeviceName = "", DeviceType = DeviceType.Web }).ToUrl("/api/v1/qr-login/start");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    // ── status ───────────────────────────────────────────────────────────────

    [Test]
    public async Task Status_FreshCode_ReturnsPending()
    {
        var code = await StartPairingAsync();

        var result = await Host.Scenario(x =>
        {
            x.Get.Url($"/api/v1/qr-login/status/{code}");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("status").GetString(), Is.EqualTo("Pending"));
    }

    [Test]
    public async Task Status_UnknownCode_ReturnsNotFound()
    {
        await Host.Scenario(x =>
        {
            x.Get.Url($"/api/v1/qr-login/status/{Guid.NewGuid():N}");
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }

    // ── scan ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task Scan_PendingCode_ReturnsDeviceInfoAndAdvancesStatus()
    {
        var code = await StartPairingAsync("Chrome on Windows", DeviceType.Web);
        var mobileToken = await RegisterAndLoginAsync($"qrscan{Guid.NewGuid():N}"[..15]);

        var result = await Host.Scenario(x =>
        {
            x.WithBearerToken(mobileToken);
            x.Post.Json(new QrLoginCodeDto { Code = code }).ToUrl("/api/v1/qr-login/scan");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("deviceName").GetString(), Is.EqualTo("Chrome on Windows"));

        var status = await Host.Scenario(x =>
        {
            x.Get.Url($"/api/v1/qr-login/status/{code}");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        var statusBody = await status.ReadAsJsonAsync<JsonElement>();
        Assert.That(statusBody.GetProperty("status").GetString(), Is.EqualTo("Scanned"));
    }

    [Test]
    public async Task Scan_WithoutBearerToken_ReturnsUnauthorized()
    {
        var code = await StartPairingAsync();

        await Host.Scenario(x =>
        {
            x.Post.Json(new QrLoginCodeDto { Code = code }).ToUrl("/api/v1/qr-login/scan");
            x.StatusCodeShouldBe(HttpStatusCode.Unauthorized);
        });
    }

    [Test]
    public async Task Scan_UnknownCode_ReturnsNotFound()
    {
        var mobileToken = await RegisterAndLoginAsync($"qrscanbad{Guid.NewGuid():N}"[..15]);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(mobileToken);
            x.Post.Json(new QrLoginCodeDto { Code = Guid.NewGuid().ToString("N") }).ToUrl("/api/v1/qr-login/scan");
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }

    [Test]
    public async Task Scan_AlreadyScannedCode_ReturnsNotFound()
    {
        var code = await StartPairingAsync();
        var firstScanner = await RegisterAndLoginAsync($"qrscan1st{Guid.NewGuid():N}"[..15]);
        var secondScanner = await RegisterAndLoginAsync($"qrscan2nd{Guid.NewGuid():N}"[..15]);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(firstScanner);
            x.Post.Json(new QrLoginCodeDto { Code = code }).ToUrl("/api/v1/qr-login/scan");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        // A code only accepts one scan - re-scanning (even by a different user) must fail rather
        // than silently re-assigning who gets to approve it.
        await Host.Scenario(x =>
        {
            x.WithBearerToken(secondScanner);
            x.Post.Json(new QrLoginCodeDto { Code = code }).ToUrl("/api/v1/qr-login/scan");
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }

    // ── approve ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Approve_ByScanningUser_SetsApprovedStatus()
    {
        var code = await StartPairingAsync();
        var mobileToken = await RegisterAndLoginAsync($"qrapprove{Guid.NewGuid():N}"[..15]);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(mobileToken);
            x.Post.Json(new QrLoginCodeDto { Code = code }).ToUrl("/api/v1/qr-login/scan");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        await Host.Scenario(x =>
        {
            x.WithBearerToken(mobileToken);
            x.Post.Json(new ApproveQrLoginDto { Code = code, Approve = true }).ToUrl("/api/v1/qr-login/approve");
            x.StatusCodeShouldBe(HttpStatusCode.NoContent);
        });

        var status = await Host.Scenario(x =>
        {
            x.Get.Url($"/api/v1/qr-login/status/{code}");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        var statusBody = await status.ReadAsJsonAsync<JsonElement>();
        Assert.That(statusBody.GetProperty("status").GetString(), Is.EqualTo("Approved"));
    }

    [Test]
    public async Task Approve_Denied_SetsDeniedStatus()
    {
        var code = await StartPairingAsync();
        var mobileToken = await RegisterAndLoginAsync($"qrdeny{Guid.NewGuid():N}"[..15]);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(mobileToken);
            x.Post.Json(new QrLoginCodeDto { Code = code }).ToUrl("/api/v1/qr-login/scan");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        await Host.Scenario(x =>
        {
            x.WithBearerToken(mobileToken);
            x.Post.Json(new ApproveQrLoginDto { Code = code, Approve = false }).ToUrl("/api/v1/qr-login/approve");
            x.StatusCodeShouldBe(HttpStatusCode.NoContent);
        });

        var status = await Host.Scenario(x =>
        {
            x.Get.Url($"/api/v1/qr-login/status/{code}");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        var statusBody = await status.ReadAsJsonAsync<JsonElement>();
        Assert.That(statusBody.GetProperty("status").GetString(), Is.EqualTo("Denied"));
    }

    [Test]
    public async Task Approve_ByDifferentUserThanScanned_ReturnsForbidden()
    {
        var code = await StartPairingAsync();
        var scanner = await RegisterAndLoginAsync($"qrscanner{Guid.NewGuid():N}"[..15]);
        var impersonator = await RegisterAndLoginAsync($"qrimposter{Guid.NewGuid():N}"[..15]);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(scanner);
            x.Post.Json(new QrLoginCodeDto { Code = code }).ToUrl("/api/v1/qr-login/scan");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        await Host.Scenario(x =>
        {
            x.WithBearerToken(impersonator);
            x.Post.Json(new ApproveQrLoginDto { Code = code, Approve = true }).ToUrl("/api/v1/qr-login/approve");
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }

    [Test]
    public async Task Approve_WithoutPriorScan_ReturnsNotFound()
    {
        var code = await StartPairingAsync();
        var token = await RegisterAndLoginAsync($"qrnoscan{Guid.NewGuid():N}"[..15]);

        await Host.Scenario(x =>
        {
            x.WithBearerToken(token);
            x.Post.Json(new ApproveQrLoginDto { Code = code, Approve = true }).ToUrl("/api/v1/qr-login/approve");
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
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
