using Messaging.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Messaging.Tests.Helpers;

/// <summary>EF Core InMemory context for unit tests.</summary>
internal sealed class TestMessagingContext : MicroserviceContext
{
    public TestMessagingContext(string dbName)
        : base(new DbContextOptionsBuilder<MicroserviceContext>()
            .UseInMemoryDatabase(dbName)
            .Options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Intentionally left empty: InMemory provider is already configured via the constructor
        // options; calling base would add a conflicting Postgres provider and throw at runtime.
    }
}
