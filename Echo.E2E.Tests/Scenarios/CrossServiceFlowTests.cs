using System.Net.Http.Json;
using Echo.E2E.Tests.Fixtures;
using Echo.E2E.Tests.Hosts;
using Npgsql;

namespace Echo.E2E.Tests.Scenarios;

/// <summary>
/// Proves cross-service wiring works for real, not just that each service boots: registering a
/// user through Identity must publish UserCreated over the real RabbitMQ broker for Social (a
/// separate real process) to consume and materialize a Profile row in its own real Postgres
/// database. Nothing here is mocked or in-process; this is the scenario the harness exists to
/// make possible at all (see the E2E test plan's Context section).
///
/// Verifies via a direct query against Social's database rather than through its authenticated
/// HTTP API: registering a user and then logging in to get a bearer token turned up a real,
/// separate, pre-existing bug (POST /api/v1/authentication/login always throws - reported
/// separately) that's a bigger fix than belongs in this harness change. Once that's fixed, an
/// HTTP-based version of this assertion (GET /api/v1/profiles/me with the token) would be a
/// stronger end-to-end proof, since it'd also cover the JWT issuance/validation chain.
/// </summary>
[TestFixture]
[Category("E2E")]
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

        var registerResponse = await _stack.Identity.Client.PostAsJsonAsync("/api/v1/authentication/register", new
        {
            Email = email,
            Password = "SecurePass123!",
            Username = "xserviceuser",
            BirthDate = DateTime.UtcNow.AddYears(-20),
        });
        Assert.That(registerResponse.IsSuccessStatusCode, Is.True,
            $"Register failed: {await registerResponse.Content.ReadAsStringAsync()}\n{_stack.Identity.CapturedOutput}");

        var body = await registerResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var userId = body.GetProperty("userId").GetString();
        Assert.That(userId, Is.Not.Null.And.Not.Empty);

        var socialConnectionString = new NpgsqlConnectionStringBuilder
        {
            Host = EchoInfraFixture.Default.PostgresHost,
            Port = EchoInfraFixture.Default.PostgresPort,
            Database = "social_xservice",
            Username = "postgres",
            Password = "postgres",
        }.ConnectionString;

        // The UserCreated event has to cross a real broker to a real, separate Social.Application
        // process and be handled there before the profile row exists - poll rather than assume
        // it's instantaneous.
        await using var connection = new NpgsqlConnection(socialConnectionString);
        await connection.OpenAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var found = false;
        while (!cts.IsCancellationRequested && !found)
        {
            await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM profiles WHERE user_id = @userId", connection);
            command.Parameters.AddWithValue("userId", userId!);
            var count = (long)(await command.ExecuteScalarAsync())!;
            found = count > 0;

            if (!found)
                await Task.Delay(500, CancellationToken.None);
        }

        Assert.That(found, Is.True,
            $"Profile was never materialized in Social's database within 30s - UserCreated likely " +
            $"never arrived over the bus.\n{_stack.Social.CapturedOutput}");
    }
}
