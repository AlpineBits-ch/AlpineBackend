using Import.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Import.Tests.Helpers;

/// <summary>EF Core InMemory context for unit tests - mirrors Guild.Tests/Helpers/TestGuildContext.cs.</summary>
internal sealed class TestImportContext : MicroserviceContext
{
    public TestImportContext(string dbName)
        : base(new DbContextOptionsBuilder<MicroserviceContext>().UseInMemoryDatabase(dbName).Options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Intentionally left empty - InMemory provider is already configured via the constructor.
    }
}
