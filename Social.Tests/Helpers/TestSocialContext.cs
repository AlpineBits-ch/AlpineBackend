using Microsoft.EntityFrameworkCore;
using Social.Infrastructure.Persistence;

namespace Social.Tests.Helpers;

/// <summary>
/// EF Core InMemory context for unit tests.
/// Overrides OnConfiguring so the base Postgres setup is never invoked.
/// Pass a unique dbName per test to keep test databases isolated.
/// </summary>
internal sealed class TestSocialContext : MicroserviceContext
{
    public TestSocialContext(string dbName)
        : base(new DbContextOptionsBuilder<MicroserviceContext>()
            .UseInMemoryDatabase(dbName)
            .Options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Intentionally left empty: InMemory provider is already configured
        // via the constructor options; calling base would add a conflicting
        // Postgres provider and throw at runtime.
    }
}
