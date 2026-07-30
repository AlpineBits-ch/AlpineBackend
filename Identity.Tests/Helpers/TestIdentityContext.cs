using Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Identity.Tests.Helpers;

/// <summary>
/// EF Core InMemory context for unit tests, mirroring Guild.Tests/Helpers/TestGuildContext.
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
