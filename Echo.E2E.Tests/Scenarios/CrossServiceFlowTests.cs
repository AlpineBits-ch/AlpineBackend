using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Echo.E2E.Tests.Fixtures;
using Echo.E2E.Tests.Hosts;

namespace Echo.E2E.Tests.Scenarios;

/// <summary>
/// Proves cross-service wiring works for real, not just that each service boots: registering a user
/// through Identity must (a) publish UserCreated over the real RabbitMQ broker for Social (a
/// separate real process) to consume and materialize a Profile, and (b) mint a JWT that Social's
/// own JwtBearer middleware - in a third real process - accepts by fetching Identity's real
/// /.well-known/openid-configuration + jwks over real HTTP.
/// </summary>
[TestFixture]
public class CrossServiceFlowTests
{
    private EchoTestStack _stack = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        _stack = await EchoTestStack.StartAsync(EchoInfraFixture.Default, "xservice", "xservice-test-instance");
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        if (_stack is not null)
            await _stack.DisposeAsync();
    }

    [Test]
    public async Task RegisterUser_ProfileIsMaterializedInSocial_ViaRealRabbitMqEvent()
    {
        var email = $"xservice-{Guid.NewGuid()}@example.com";
        var password = "SecurePass123!";

        var registerResponse = await _stack.Identity.Client.PostAsJsonAsync("/api/v1/authentication/register", new
        {
            Email = email,
            Password = password,
            Username = "xserviceuser",
            BirthDate = DateTime.UtcNow.AddYears(-20),
        });
        Assert.That(registerResponse.IsSuccessStatusCode, Is.True,
            $"Register failed: {await registerResponse.Content.ReadAsStringAsync()}\n{_stack.Identity.CapturedOutput}");

        var loginResponse = await _stack.Identity.Client.PostAsJsonAsync("/api/v1/authentication/login", new
        {
            Email = email,
            Password = password,
        });
        Assert.That(loginResponse.IsSuccessStatusCode, Is.True,
            $"Login failed: {await loginResponse.Content.ReadAsStringAsync()}\n{_stack.Identity.CapturedOutput}");

        var loginBody = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = loginBody.GetProperty("access_token").GetString();
        Assert.That(accessToken, Is.Not.Null.And.Not.Empty, "Login response should carry an access_token");

        _stack.Social.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // The UserCreated event has to cross a real broker to a real, separate Social.Application
        // process and be handled there before the profile exists - poll rather than assume it's
        // instantaneous.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        HttpResponseMessage? profileResponse = null;
        while (!cts.IsCancellationRequested)
        {
            profileResponse = await _stack.Social.Client.GetAsync("/api/v1/profiles/me", cts.Token);
            if (profileResponse.IsSuccessStatusCode)
                break;

            await Task.Delay(500, CancellationToken.None);
        }

        Assert.That(profileResponse, Is.Not.Null);
        Assert.That(profileResponse!.IsSuccessStatusCode, Is.True,
            $"Profile was never materialized in Social within 30s (last status {profileResponse.StatusCode}) - " +
            $"UserCreated likely never arrived over the bus.\n{_stack.Social.CapturedOutput}");
    }
}
