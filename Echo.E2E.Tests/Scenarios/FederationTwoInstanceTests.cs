using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Echo.E2E.Tests.Hosts;

namespace Echo.E2E.Tests.Scenarios;

/// <summary>
/// Two fully independent Echo instances (own Postgres/RabbitMQ/Redis/Scylla, own Ed25519
/// federation keypair - see FederationInstancePair) federating with each other for real. This is
/// the scenario the whole harness exists to make possible: nothing before this pass could boot
/// two real, separately-deployed-shaped instances and prove they can actually talk.
/// </summary>
[TestFixture]
[Category("E2E")]
public class FederationTwoInstanceTests
{
    private FederationInstancePair _pair = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        _pair = await FederationInstancePair.StartAsync("instance-a-test", "instance-b-test");
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        if (_pair is not null)
            await _pair.DisposeAsync();
    }

    private static async Task<string> RegisterAndGetTokenAsync(SpawnedServiceProcess identity, string username)
    {
        var email = $"{username}-{Guid.NewGuid()}@example.com";
        const string password = "SecurePass123!";

        var register = await identity.Client.PostAsJsonAsync("/api/v1/authentication/register", new
        {
            Email = email,
            Password = password,
            Username = username,
            BirthDate = DateTime.UtcNow.AddYears(-20),
        });
        Assert.That(register.IsSuccessStatusCode, Is.True,
            $"Register failed: {await register.Content.ReadAsStringAsync()}\n{identity.CapturedOutput}");

        var token = await identity.Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = password,
            ["client_id"] = "echo",
        }));
        Assert.That(token.IsSuccessStatusCode, Is.True,
            $"Token request failed: {await token.Content.ReadAsStringAsync()}\n{identity.CapturedOutput}");

        var body = await token.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("access_token").GetString()!;
    }

    [Test]
    public async Task Handshake_BothInstancesLandActive()
    {
        var tokenA = await RegisterAndGetTokenAsync(_pair.A.Identity, "admin_a");
        _pair.A.Federation.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);

        var initiateResponse = await _pair.A.Federation.Client.PostAsJsonAsync(
            "/api/v1/admin/federation/initiate",
            new { TargetHost = $"http://127.0.0.1:{_pair.B.Federation.Port}" });
        Assert.That(initiateResponse.IsSuccessStatusCode, Is.True,
            $"Handshake initiation failed: {await initiateResponse.Content.ReadAsStringAsync()}\n{_pair.A.Federation.CapturedOutput}");

        // A's side records B as Active synchronously in the initiate call. B's side (the inbound
        // receiver) applies its acceptance policy (AutoAccept by default) in the same request,
        // but confirm by polling A's own admin list endpoint rather than assuming timing.
        //
        // The instance identity B reports of itself (Env.GeneralConfiguration.InstanceUrl, i.e.
        // the "Host" recorded on A's side) is B's INSTANCE_URL - which every service in a stack
        // shares as the OIDC issuer - not the port the Federation service itself happens to be
        // listening on. In this harness INSTANCE_URL is pinned to the Identity service's port
        // (see EchoTestStack), so that's what A ends up storing as B's host, even though the
        // handshake POST itself was dialed against B's Federation port.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var isActive = false;
        while (!cts.IsCancellationRequested && !isActive)
        {
            var instances = await _pair.A.Federation.Client.GetFromJsonAsync<JsonElement>(
                "/api/v1/admin/federation/instances", cts.Token);
            isActive = instances.EnumerateArray().Any(i =>
                i.GetProperty("host").GetString() == $"http://127.0.0.1:{_pair.B.Identity.Port}" &&
                i.GetProperty("status").GetString() == "Active");

            if (!isActive)
                await Task.Delay(500, CancellationToken.None);
        }

        Assert.That(isActive, Is.True,
            $"Instance A never recorded instance B as Active.\n{_pair.A.Federation.CapturedOutput}");
    }
}
