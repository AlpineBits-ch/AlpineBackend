using Echo.E2E.Tests.Fixtures;

// Intentionally NOT in the Fixtures namespace: NUnit only auto-invokes a [SetUpFixture] for
// tests in its own namespace or a nested one. Declaring it at the assembly root namespace makes
// it apply to every test in Echo.E2E.Tests (Scenarios, Hosts, etc).
namespace Echo.E2E.Tests;

/// <summary>
/// Boots the default shared <see cref="EchoInfraSet"/> once per assembly, for ordinary
/// single-instance cross-service scenario tests. Two-instance federation tests provision their
/// own independent <see cref="EchoInfraSet"/> via <see cref="EchoInfraSet.StartAsync"/> instead
/// of using this one - see the type doc on <see cref="EchoInfraSet"/> for why.
/// </summary>
[SetUpFixture]
public class EchoInfraFixture
{
    public static EchoInfraSet Default { get; private set; } = null!;

    // One database per service, matching compose's init-multiple-dbs.sh convention.
    public static readonly string[] DatabaseNames =
    [
        "identity_e2e", "guild_e2e", "messaging_e2e", "social_e2e", "federation_e2e", "import_e2e", "echo_e2e"
    ];

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        Default = await EchoInfraSet.StartAsync();
        await Default.CreateDatabasesAsync(DatabaseNames);
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        await Default.DisposeAsync();
    }
}
