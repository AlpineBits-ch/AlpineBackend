using System.Net.Http.Json;
using Echo.E2E.Tests.Fixtures;
using Echo.E2E.Tests.Hosts;
using Npgsql;
using Echo.E2E.Tests.Support;

namespace Echo.E2E.Tests.Scenarios;

/// <summary>
/// Proves cross-service wiring works for real, not just that each service boots: registering a user
/// through Identity must publish UserCreated over the real RabbitMQ broker for Social (a separate
/// real process) to consume and materialize a Profile row in its own real Postgres database.
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
        await E2EAssert.SucceededAsync(registerResponse, _stack.Identity, "Register failed");

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

        E2EAssert.Held(found, _stack.Social,
            "Profile was never materialized in Social's database within 30s - UserCreated likely "
            + "never arrived over the bus.");
    }
}
