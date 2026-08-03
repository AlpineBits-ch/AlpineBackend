using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Echo.E2E.Tests.Fixtures;
using Echo.E2E.Tests.Hosts;
using Echo.E2E.Tests.Support;
using Npgsql;

namespace Echo.E2E.Tests.Scenarios;

/// <summary>
/// Two fully independent Echo instances (own Postgres/RabbitMQ/Redis/Scylla, own Ed25519 federation
/// keypair - see FederationInstancePair) federating with each other for real.
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

    /// <summary>
    /// Registers a user and promotes them to <c>UserType.Admin</c> directly in Identity's database.
    /// </summary>
    private static async Task<string> RegisterAdminAndGetTokenAsync(
        EchoInfraSet infra, SpawnedServiceProcess identity, string username)
    {
        var token = await RegisterAndGetTokenAsync(identity, username);

        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = infra.PostgresHost,
            Port = infra.PostgresPort,
            Database = "identity_e2e",
            Username = "postgres",
            Password = "postgres",
        }.ConnectionString;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE asp_net_users SET user_type = 'admin'::user_type WHERE user_name = @u", connection);
        command.Parameters.AddWithValue("u", username);

        var affected = await command.ExecuteNonQueryAsync();
        Assert.That(affected, Is.EqualTo(1), $"Failed to promote '{username}' to admin for the federation admin routes.");

        // The admin check is resolved from the database per request (not from a token claim), so
        // the token minted before the promotion is still fine.
        return token;
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
        await E2EAssert.SucceededAsync(register, identity, "Register failed");

        var token = await identity.Client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = password,
            ["client_id"] = "echo",
        }));
        await E2EAssert.SucceededAsync(token, identity, "Token request failed");

        var body = await token.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("access_token").GetString()!;
    }

    [Test]
    public async Task Handshake_BothInstancesLandActive()
    {
        var tokenA = await RegisterAdminAndGetTokenAsync(_pair.InfraA, _pair.A.Identity, "admin_a");
        _pair.A.Federation.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);

        var initiateResponse = await _pair.A.Federation.Client.PostAsJsonAsync(
            "/api/v1/admin/federation/initiate",
            new { TargetHost = $"http://127.0.0.1:{_pair.B.Federation.Port}" });
        await E2EAssert.SucceededAsync(initiateResponse, _pair.A.Federation, "Handshake initiation failed");

        // A's side records B as Active synchronously in the initiate call.
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

        E2EAssert.Held(isActive, _pair.A.Federation, $"Instance A never recorded instance B as Active.");
    }
}
