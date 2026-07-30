using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Tests.Helpers;

/// <summary>
/// EF Core InMemory context for unit tests, mirroring Guild.Tests/Helpers/TestGuildContext.
/// Overrides OnConfiguring so the base Postgres/Npgsql setup is never invoked.
/// Pass a unique dbName per test to keep test databases isolated.
/// </summary>
internal sealed class TestIdentityContext : MicroserviceContext
{
    public TestIdentityContext(string dbName)
        : base(new DbContextOptionsBuilder<MicroserviceContext>()
            .UseInMemoryDatabase(dbName)
            .Options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Intentionally left empty: InMemory provider is already configured via the constructor
        // options; calling base would add a conflicting Npgsql provider and throw at runtime.
    }
}
