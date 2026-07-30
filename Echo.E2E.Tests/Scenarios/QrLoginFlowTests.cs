using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Echo.E2E.Tests.Fixtures;
using Echo.E2E.Tests.Hosts;

namespace Echo.E2E.Tests.Scenarios;

/// <summary>
/// Proves the QR cross-device login flow end to end against a real spawned Identity process (real
/// Postgres + Redis, not the InMemory-cache substitute Identity.Tests uses): an unauthenticated
/// "desktop" starts a pairing code, an authenticated "mobile" scans and approves it, the desktop
/// redeems the approval at /connect/token for its own independent access+refresh pair, and the new
/// session shows up (and is revocable) via /api/v1/sessions from either device.
/// </summary>
[TestFixture]
[Category("E2E")]
public class QrLoginFlowTests
{
    private EchoTestStack _stack = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        _stack = await EchoTestStack.StartAsync(EchoInfraFixture.Default, "qrlogin", "qrlogin-test-instance");
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        if (_stack is not null)
            await _stack.DisposeAsync();
    }

    private static async Task<string> RegisterAsync(SpawnedServiceProcess identity, string username, string password)
    {
        var register = await identity.Client.PostAsJsonAsync("/api/v1/authentication/register", new
        {
            Email = $"{username}-{Guid.NewGuid()}@example.com",
            Password = password,
            Username = username,
            BirthDate = DateTime.UtcNow.AddYears(-20),
        });
        Assert.That(register.IsSuccessStatusCode, Is.True,
            $"Register failed: {await register.Content.ReadAsStringAsync()}\n{identity.CapturedOutput}");
        return (await register.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userId").GetString()!;
    }

    private static async Task<(string access, string? refresh)> LoginAsync(
        SpawnedServiceProcess identity, string username, string password, bool offlineAccess = false)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = password,
            ["client_id"] = "echo",
        };
        if (offlineAccess) form["scope"] = "offline_access";

        var response = await identity.Client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        Assert.That(response.IsSuccessStatusCode, Is.True,
            $"Login failed: {await response.Content.ReadAsStringAsync()}\n{identity.CapturedOutput}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var refresh = body.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
        return (body.GetProperty("access_token").GetString()!, refresh);
    }

    private static HttpClient AuthedClient(SpawnedServiceProcess service, string token)
    {
        var client = new HttpClient { BaseAddress = service.Client.BaseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Test]
    public async Task FullQrLoginFlow_ApprovedCode_IssuesIndependentSessionVisibleToBothDevices()
    {
        const string username = "qrflowuser";
        const string password = "SecurePass123!";

        await RegisterAsync(_stack.Identity, username, password);
        var (mobileToken, _) = await LoginAsync(_stack.Identity, username, password);
        using var mobile = AuthedClient(_stack.Identity, mobileToken);

        // --- Desktop (unauthenticated) starts a pairing code. ---
        var startResponse = await _stack.Identity.Client.PostAsJsonAsync("/api/v1/qr-login/start",
            new { DeviceName = "Echo Desktop - E2E", DeviceType = "Desktop" });
        Assert.That(startResponse.IsSuccessStatusCode, Is.True,
            $"Start failed: {await startResponse.Content.ReadAsStringAsync()}\n{_stack.Identity.CapturedOutput}");
        var startBody = await startResponse.Content.ReadFromJsonAsync<JsonElement>();
        var code = startBody.GetProperty("code").GetString()!;

        async Task<string> PollStatusAsync()
        {
            var statusResponse = await _stack.Identity.Client.GetAsync($"/api/v1/qr-login/status/{code}");
            Assert.That(statusResponse.IsSuccessStatusCode, Is.True,
                $"Status failed: {await statusResponse.Content.ReadAsStringAsync()}");
            return (await statusResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString()!;
        }

        Assert.That(await PollStatusAsync(), Is.EqualTo("Pending"));

        // --- Mobile (authenticated) scans it. ---
        var scanResponse = await mobile.PostAsJsonAsync("/api/v1/qr-login/scan", new { Code = code });
        Assert.That(scanResponse.IsSuccessStatusCode, Is.True,
            $"Scan failed: {await scanResponse.Content.ReadAsStringAsync()}\n{_stack.Identity.CapturedOutput}");
        var scanBody = await scanResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(scanBody.GetProperty("deviceName").GetString(), Is.EqualTo("Echo Desktop - E2E"));
        Assert.That(await PollStatusAsync(), Is.EqualTo("Scanned"));

        // --- Mobile approves it. ---
        var approveResponse = await mobile.PostAsJsonAsync("/api/v1/qr-login/approve", new { Code = code, Approve = true });
        Assert.That(approveResponse.IsSuccessStatusCode, Is.True,
            $"Approve failed: {await approveResponse.Content.ReadAsStringAsync()}\n{_stack.Identity.CapturedOutput}");
        Assert.That(await PollStatusAsync(), Is.EqualTo("Approved"));

        // --- Desktop redeems the approval for its own independent tokens. ---
        var exchangeResponse = await _stack.Identity.Client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "urn:echo:params:oauth:grant-type:qr_login",
                ["qr_code"] = code,
                ["client_id"] = "echo",
            }));
        Assert.That(exchangeResponse.IsSuccessStatusCode, Is.True,
            $"Exchange failed: {await exchangeResponse.Content.ReadAsStringAsync()}\n{_stack.Identity.CapturedOutput}");
        var exchangeBody = await exchangeResponse.Content.ReadFromJsonAsync<JsonElement>();
        var desktopToken = exchangeBody.GetProperty("access_token").GetString()!;
        Assert.That(desktopToken, Is.Not.Null.And.Not.Empty);
        Assert.That(desktopToken, Is.Not.EqualTo(mobileToken), "The desktop must get its own token, not the mobile session's.");

        // Single-use: redeeming the same approved code again must fail.
        var replayResponse = await _stack.Identity.Client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "urn:echo:params:oauth:grant-type:qr_login",
                ["qr_code"] = code,
                ["client_id"] = "echo",
            }));
        Assert.That(replayResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        // --- Both devices now see two live sessions, and each recognizes its own. ---
        using var desktop = AuthedClient(_stack.Identity, desktopToken);

        var mobileSessionsResponse = await mobile.GetAsync("/api/v1/sessions");
        var mobileSessions = (await mobileSessionsResponse.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.That(mobileSessions, Has.Count.EqualTo(2));
        Assert.That(mobileSessions.Count(s => s.GetProperty("isCurrent").GetBoolean()), Is.EqualTo(1));
        Assert.That(mobileSessions.Any(s => s.GetProperty("deviceName").GetString() == "Echo Desktop - E2E"), Is.True);

        var desktopSessionsResponse = await desktop.GetAsync("/api/v1/sessions");
        var desktopSessions = (await desktopSessionsResponse.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.That(desktopSessions, Has.Count.EqualTo(2));
        var desktopOwnSession = desktopSessions.Single(s => s.GetProperty("isCurrent").GetBoolean());
        Assert.That(desktopOwnSession.GetProperty("deviceName").GetString(), Is.EqualTo("Echo Desktop - E2E"));
        Assert.That(desktopOwnSession.GetProperty("deviceType").GetString(), Is.EqualTo("Desktop"));
    }

    [Test]
    public async Task Denied_DesktopCannotExchangeCode()
    {
        const string username = "qrdenyuser";
        const string password = "SecurePass123!";

        await RegisterAsync(_stack.Identity, username, password);
        var (mobileToken, _) = await LoginAsync(_stack.Identity, username, password);
        using var mobile = AuthedClient(_stack.Identity, mobileToken);

        var startResponse = await _stack.Identity.Client.PostAsJsonAsync("/api/v1/qr-login/start",
            new { DeviceName = "Untrusted Desktop", DeviceType = "Web" });
        var code = (await startResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

        await mobile.PostAsJsonAsync("/api/v1/qr-login/scan", new { Code = code });
        var denyResponse = await mobile.PostAsJsonAsync("/api/v1/qr-login/approve", new { Code = code, Approve = false });
        Assert.That(denyResponse.IsSuccessStatusCode, Is.True,
            $"Deny failed: {await denyResponse.Content.ReadAsStringAsync()}\n{_stack.Identity.CapturedOutput}");

        var exchangeResponse = await _stack.Identity.Client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "urn:echo:params:oauth:grant-type:qr_login",
                ["qr_code"] = code,
                ["client_id"] = "echo",
            }));
        Assert.That(exchangeResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
            "A denied code must never be redeemable for tokens.");
    }

    [Test]
    public async Task RevokingQrSession_BlocksItsNextRefresh()
    {
        const string username = "qrrevokeuser";
        const string password = "SecurePass123!";

        await RegisterAsync(_stack.Identity, username, password);
        var (mobileToken, _) = await LoginAsync(_stack.Identity, username, password);
        using var mobile = AuthedClient(_stack.Identity, mobileToken);

        var startResponse = await _stack.Identity.Client.PostAsJsonAsync("/api/v1/qr-login/start",
            new { DeviceName = "Revoke Target Desktop", DeviceType = "Desktop" });
        var code = (await startResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        await mobile.PostAsJsonAsync("/api/v1/qr-login/scan", new { Code = code });
        await mobile.PostAsJsonAsync("/api/v1/qr-login/approve", new { Code = code, Approve = true });

        var exchangeResponse = await _stack.Identity.Client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "urn:echo:params:oauth:grant-type:qr_login",
                ["qr_code"] = code,
                ["client_id"] = "echo",
                ["scope"] = "offline_access",
            }));
        var exchangeBody = await exchangeResponse.Content.ReadFromJsonAsync<JsonElement>();
        var desktopRefreshToken = exchangeBody.GetProperty("refresh_token").GetString()!;

        // Mobile finds the newly-paired desktop session in its own list and revokes it.
        var sessionsResponse = await mobile.GetAsync("/api/v1/sessions");
        var sessions = (await sessionsResponse.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        var desktopSessionId = sessions.Single(s => s.GetProperty("deviceName").GetString() == "Revoke Target Desktop")
            .GetProperty("id").GetString();

        var revokeResponse = await mobile.DeleteAsync($"/api/v1/sessions/{desktopSessionId}");
        Assert.That(revokeResponse.StatusCode, Is.EqualTo(HttpStatusCode.NoContent),
            $"Revoke failed: {await revokeResponse.Content.ReadAsStringAsync()}\n{_stack.Identity.CapturedOutput}");

        var refreshResponse = await _stack.Identity.Client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = desktopRefreshToken,
                ["client_id"] = "echo",
            }));
        Assert.That(refreshResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
            "A revoked session must not be able to refresh its access token.");
    }
}
