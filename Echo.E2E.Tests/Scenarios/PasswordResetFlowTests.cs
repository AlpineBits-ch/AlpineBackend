using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Echo.E2E.Tests.Fixtures;
using Echo.E2E.Tests.Hosts;
using StackExchange.Redis;
using Echo.E2E.Tests.Support;

namespace Echo.E2E.Tests.Scenarios;

/// <summary>
/// Proves the "forgot password" flow end to end: requesting a reset generates a real short code in
/// the same shared Redis instance Identity itself reads back from (read directly here via
/// StackExchange.Redis, the same "assert against real infra directly" pattern
/// AccountDeletionFlowTests uses for Postgres - there's no dev/fake email sink wired up in this
/// harness, so the emailed code has to be observed at its storage layer instead), resetting the
/// password with that code, and then confirming the OLD password no longer authenticates while the
/// NEW one does via the real <c>/connect/token</c> password grant.
/// </summary>
[TestFixture]
[Category("E2E")]
public class PasswordResetFlowTests
{
    private EchoTestStack _stack = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        _stack = await EchoTestStack.StartAsync(EchoInfraFixture.Default, "pwreset", "pwreset-test-instance");
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        if (_stack is not null)
            await _stack.DisposeAsync();
    }

    private static async Task<string> RegisterAsync(SpawnedServiceProcess identity, string username, string email, string password)
    {
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

    private static async Task<bool> TryLoginAsync(SpawnedServiceProcess identity, string username, string password)
    {
        var tokenResponse = await identity.Client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = username,
                ["password"] = password,
                ["client_id"] = "echo",
            }));
        return tokenResponse.IsSuccessStatusCode;
    }

    /// <summary>Reads the reset code straight out of the shared Redis instance - IDistributedCache's
    /// StackExchangeRedis implementation stores each entry as a Redis hash (fields "data",
    /// "absexp", "sldexp") keyed by the cache key verbatim (no InstanceName prefix is configured -
    /// see Identity.Application/Program.cs), so a plain HGET reproduces exactly what
    /// PasswordResetCodeService.GetOrCreateCodeAsync/IDistributedCache.GetStringAsync would read.</summary>
    private static async Task<string?> ReadPasswordResetCodeAsync(string email)
    {
        var infra = EchoInfraFixture.Default;
        var config = new ConfigurationOptions
        {
            EndPoints = { { infra.RedisHost, infra.RedisPort } },
            Password = EchoInfraSet.RedisPassword,
        };
        await using var redis = await ConnectionMultiplexer.ConnectAsync(config);
        var db = redis.GetDatabase();
        var data = await db.HashGetAsync($"password_reset_code:{email}", "data");
        return data.IsNullOrEmpty ? null : System.Text.Encoding.UTF8.GetString((byte[])data!);
    }

    [Test]
    public async Task RequestReset_ThenResetWithCode_OldPasswordFailsNewPasswordWorks()
    {
        var username = "pwresetuser";
        var email = $"{username}-{Guid.NewGuid()}@example.com";
        const string oldPassword = "SecurePass123!";
        const string newPassword = "EvenMoreSecurePass456!";

        await RegisterAsync(_stack.Identity, username, email, oldPassword);

        // Sanity: old password works before we touch anything.
        Assert.That(await TryLoginAsync(_stack.Identity, username, oldPassword), Is.True,
            "Old password should work before requesting a reset.");

        // --- Act: request a reset. ---

        var requestResponse = await _stack.Identity.Client.GetAsync(
            $"/api/v1/user/request-password-reset?email={Uri.EscapeDataString(email)}");
        await E2EAssert.HasStatusAsync(requestResponse, HttpStatusCode.Accepted, _stack.Identity,
            "Request password reset failed");

        var code = await ReadPasswordResetCodeAsync(email);
        Assert.That(code, Is.Not.Null.And.Not.Empty,
            "Reset code was never written to the shared cache - RequestPasswordReset either didn't run or crashed before caching the code.");

        // Requesting again while the code is still valid must return the SAME code (per the
        // frontend guide's documented "resend just re-sends" behavior), not silently mint a new one.
        var requestAgainResponse = await _stack.Identity.Client.GetAsync(
            $"/api/v1/user/request-password-reset?email={Uri.EscapeDataString(email)}");
        Assert.That(requestAgainResponse.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var codeAfterResend = await ReadPasswordResetCodeAsync(email);
        Assert.That(codeAfterResend, Is.EqualTo(code), "Resending the reset request must not invalidate the original code.");

        // --- Act: reset with a wrong code first - must not succeed. ---

        var wrongCodeResponse = await _stack.Identity.Client.PostAsJsonAsync("/api/v1/user/reset-password", new
        {
            Email = email,
            Code = "wrongc",
            NewPassword = newPassword,
        });
        Assert.That(wrongCodeResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
            "An incorrect reset code must not be accepted.");

        // --- Act: reset with the real code. ---

        var resetResponse = await _stack.Identity.Client.PostAsJsonAsync("/api/v1/user/reset-password", new
        {
            Email = email,
            Code = code,
            NewPassword = newPassword,
        });
        await E2EAssert.SucceededAsync(resetResponse, _stack.Identity, "Reset password failed");

        // --- Assert: old password no longer works, new one does. ---

        Assert.That(await TryLoginAsync(_stack.Identity, username, oldPassword), Is.False,
            "Old password must stop working after a successful reset.");
        Assert.That(await TryLoginAsync(_stack.Identity, username, newPassword), Is.True,
            "New password must work after a successful reset.");

        // The code is single-use - it must be gone from the cache and rejected on replay.
        var codeAfterReset = await ReadPasswordResetCodeAsync(email);
        Assert.That(codeAfterReset, Is.Null, "The reset code must be removed from cache after a successful reset.");

        var replayResponse = await _stack.Identity.Client.PostAsJsonAsync("/api/v1/user/reset-password", new
        {
            Email = email,
            Code = code,
            NewPassword = "AnotherPassword789!",
        });
        Assert.That(replayResponse.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
            "A used reset code must not be replayable.");
    }
}
