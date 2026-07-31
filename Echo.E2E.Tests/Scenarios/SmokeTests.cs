using System.Net.Http.Json;
using Echo.E2E.Tests.Fixtures;
using Echo.E2E.Tests.Hosts;
using Echo.E2E.Tests.Support;

namespace Echo.E2E.Tests.Scenarios;

/// <summary>
/// Proves the harness itself works before later scenarios build on it: a real Identity process,
/// backed by a real (Testcontainers) Postgres, can register a user over real HTTP.
/// </summary>
[TestFixture]
[Category("E2E")]
public class SmokeTests
{
    private EchoTestStack _stack = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        _stack = await EchoTestStack.StartAsync(EchoInfraFixture.Default, "smoke", "smoke-test-instance");
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        // _stack stays null if SetUpAsync threw partway through EchoTestStack.StartAsync -
        // that method cleans up its own partially-started processes on failure, so there's
        // nothing left here to dispose.
        if (_stack is not null)
            await _stack.DisposeAsync();
    }

    [Test]
    public async Task AllServices_BecomeHealthy()
    {
        Assert.That(_stack.Identity.Port, Is.GreaterThan(0));
        Assert.That(_stack.Guild.Port, Is.GreaterThan(0));
        Assert.That(_stack.Messaging.Port, Is.GreaterThan(0));
        Assert.That(_stack.Social.Port, Is.GreaterThan(0));
        Assert.That(_stack.Federation.Port, Is.GreaterThan(0));
        Assert.That(_stack.Import.Port, Is.GreaterThan(0));
        Assert.That(_stack.Gateway.Port, Is.GreaterThan(0));
    }

    [Test]
    public async Task RegisterUser_AgainstRealIdentityProcess_Returns200WithUserId()
    {
        var request = new
        {
            Email = $"e2e-{Guid.NewGuid()}@example.com",
            Password = "SecurePass123!",
            Username = "e2euser",
            BirthDate = DateTime.UtcNow.AddYears(-20),
        };

        var response = await _stack.Identity.Client.PostAsJsonAsync("/api/v1/authentication/register", request);

        await E2EAssert.SucceededAsync(response, _stack.Identity, "Register failed");
    }
}
