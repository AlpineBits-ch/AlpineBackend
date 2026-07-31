using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Echo.E2E.Tests.Fixtures;
using Echo.E2E.Tests.Hosts;
using Echo.E2E.Tests.Support;

namespace Echo.E2E.Tests.Scenarios;

/// <summary>
/// Proves authenticator-app MFA end to end: enroll returns a real Base32 TOTP secret, a code
/// generated from that secret in-test (via the RFC 6238 <see cref="Totp"/> helper - there's no
/// OtpNet-equivalent package already referenced anywhere in this repo, so a small
/// Identity-Core-compatible generator lives in Support/Totp.cs instead of adding one) enables
/// MFA, and the real OAuth2 password grant at /connect/token then requires it: no code -> 401
/// mfa_required, wrong code -> 401 mfa_invalid, correct code -> success, and finally a recovery
/// code works once as a fallback and is rejected on reuse.
/// </summary>
[TestFixture]
[Category("E2E")]
public class MfaFlowTests
{
    private EchoTestStack _stack = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        _stack = await EchoTestStack.StartAsync(EchoInfraFixture.Default, "mfa", "mfa-test-instance");
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        if (_stack is not null)
            await _stack.DisposeAsync();
    }

    private static async Task<string> RegisterAsync(SpawnedServiceProcess identity, string username, string password)
    {
        var email = $"{username}-{Guid.NewGuid()}@example.com";
        var register = await identity.Client.PostAsJsonAsync("/api/v1/authentication/register", new
        {
            Email = email,
            Password = password,
            Username = username,
            BirthDate = DateTime.UtcNow.AddYears(-20),
        });
        await E2EAssert.SucceededAsync(register, identity, "Register failed");
        var registerBody = await register.Content.ReadFromJsonAsync<JsonElement>();
        return registerBody.GetProperty("userId").GetString()!;
    }

    private static async Task<HttpResponseMessage> TryTokenAsync(
        SpawnedServiceProcess identity, string username, string password, string? mfaCode)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = password,
            ["client_id"] = "echo",
        };
        if (mfaCode is not null) form["mfa_code"] = mfaCode;

        return await identity.Client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
    }

    private static async Task<string> GetAccessTokenAsync(SpawnedServiceProcess identity, string username, string password)
    {
        var response = await TryTokenAsync(identity, username, password, mfaCode: null);
        await E2EAssert.SucceededAsync(response, identity, "Token request failed");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("access_token").GetString()!;
    }

    private static HttpClient AuthedClient(SpawnedServiceProcess service, string token)
    {
        var client = new HttpClient { BaseAddress = service.Client.BaseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Test]
    public async Task Enroll_Enable_LoginRequiresCode_ThenRecoveryCodeFallback()
    {
        const string username = "mfauser";
        const string password = "SecurePass123!";

        await RegisterAsync(_stack.Identity, username, password);
        var token = await GetAccessTokenAsync(_stack.Identity, username, password);
        using var identity = AuthedClient(_stack.Identity, token);

        // --- Step 1: enroll - mints a pending secret, MFA not yet enabled. ---

        var enrollResponse = await identity.PostAsync("/api/v1/user/mfa/enroll", null);
        await E2EAssert.SucceededAsync(enrollResponse, _stack.Identity, "Enroll failed");
        var enrollBody = await enrollResponse.Content.ReadFromJsonAsync<JsonElement>();
        var secret = enrollBody.GetProperty("secret").GetString()!;
        Assert.That(enrollBody.GetProperty("otpAuthUri").GetString(), Does.Contain("otpauth://totp/"));

        // Enrolling again before /enable must re-return the same pending secret, not mint a new one.
        var enrollAgainResponse = await identity.PostAsync("/api/v1/user/mfa/enroll", null);
        var enrollAgainBody = await enrollAgainResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(enrollAgainBody.GetProperty("secret").GetString(), Is.EqualTo(secret));

        // MFA must not be required yet - a plain login (no mfa_code) still works at this point.
        var preEnableLogin = await TryTokenAsync(_stack.Identity, username, password, mfaCode: null);
        Assert.That(preEnableLogin.IsSuccessStatusCode, Is.True,
            "MFA must not be enforced until /mfa/enable actually confirms a working code.");

        // --- Step 2: confirm with a real code from the secret, wrong code first. ---

        var enableWrongResponse = await identity.PostAsJsonAsync("/api/v1/user/mfa/enable", new { Code = "000000" });
        Assert.That(enableWrongResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
            "An incorrect enrollment code must be rejected.");

        var validCode = Totp.GenerateCode(secret);
        var enableResponse = await identity.PostAsJsonAsync("/api/v1/user/mfa/enable", new { Code = validCode });
        await E2EAssert.SucceededAsync(enableResponse, _stack.Identity, "Enable MFA failed");
        var enableBody = await enableResponse.Content.ReadFromJsonAsync<JsonElement>();
        var recoveryCodes = enableBody.GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.That(recoveryCodes, Has.Count.EqualTo(8));

        // --- Step 3: login now requires a code. ---

        var noCodeResponse = await TryTokenAsync(_stack.Identity, username, password, mfaCode: null);
        Assert.That(noCodeResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
            "Login without an MFA code must be rejected once MFA is enabled.");
        Assert.That(await noCodeResponse.Content.ReadAsStringAsync(), Does.Contain("mfa_required"));

        var wrongCodeResponse = await TryTokenAsync(_stack.Identity, username, password, mfaCode: "000000");
        Assert.That(wrongCodeResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
            "Login with a wrong MFA code must be rejected.");
        Assert.That(await wrongCodeResponse.Content.ReadAsStringAsync(), Does.Contain("mfa_invalid"));

        var wrongPasswordResponse = await TryTokenAsync(_stack.Identity, "not-" + username, password, mfaCode: null);
        Assert.That(await wrongPasswordResponse.Content.ReadAsStringAsync(), Does.Not.Contain("mfa_"),
            "A wrong username/password must look like an ordinary auth failure, not leak MFA state.");

        var correctCode = Totp.GenerateCode(secret);
        var correctCodeResponse = await TryTokenAsync(_stack.Identity, username, password, mfaCode: correctCode);
        await E2EAssert.SucceededAsync(correctCodeResponse, _stack.Identity, "Login with a correct MFA code should succeed");

        // --- Step 4: recovery code fallback - works once, then is rejected on reuse. ---

        var recoveryCode = recoveryCodes[0];
        var recoveryLoginResponse = await TryTokenAsync(_stack.Identity, username, password, mfaCode: recoveryCode);
        await E2EAssert.SucceededAsync(recoveryLoginResponse, _stack.Identity, "Login with a valid recovery code should succeed");

        var recoveryReuseResponse = await TryTokenAsync(_stack.Identity, username, password, mfaCode: recoveryCode);
        Assert.That(recoveryReuseResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
            "A recovery code must be single-use - reusing it must be rejected.");
        Assert.That(await recoveryReuseResponse.Content.ReadAsStringAsync(), Does.Contain("mfa_invalid"));
    }

    [Test]
    public async Task DisableMfa_RequiresPassword_ThenLoginNoLongerNeedsCode()
    {
        const string username = "mfadisableuser";
        const string password = "SecurePass123!";

        await RegisterAsync(_stack.Identity, username, password);
        var token = await GetAccessTokenAsync(_stack.Identity, username, password);
        using var identity = AuthedClient(_stack.Identity, token);

        var enrollResponse = await identity.PostAsync("/api/v1/user/mfa/enroll", null);
        var enrollBody = await enrollResponse.Content.ReadFromJsonAsync<JsonElement>();
        var secret = enrollBody.GetProperty("secret").GetString()!;

        var enableResponse = await identity.PostAsJsonAsync("/api/v1/user/mfa/enable", new { Code = Totp.GenerateCode(secret) });
        Assert.That(enableResponse.IsSuccessStatusCode, Is.True,
            $"Enable MFA failed: {await enableResponse.Content.ReadAsStringAsync()}");

        var disableWrongPasswordResponse = await identity.PostAsJsonAsync("/api/v1/user/mfa/disable", new { Password = "WrongPassword!" });
        Assert.That(disableWrongPasswordResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
            "Disabling MFA with the wrong password must be rejected.");

        var disableResponse = await identity.PostAsJsonAsync("/api/v1/user/mfa/disable", new { Password = password });
        await E2EAssert.SucceededAsync(disableResponse, _stack.Identity, "Disable MFA failed");

        var loginResponse = await TryTokenAsync(_stack.Identity, username, password, mfaCode: null);
        Assert.That(loginResponse.IsSuccessStatusCode, Is.True,
            "Login without a code must succeed again once MFA has been disabled.");
    }
}
