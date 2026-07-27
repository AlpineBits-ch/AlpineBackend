using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Bots.Infrastructure.Persistence;

public class DesignTimeFactory : IDesignTimeDbContextFactory<MicroserviceContext>
{
    public MicroserviceContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<MicroserviceContext>();

        // Dummy connection string - migrations tooling only needs the provider, not a live connection.
        return new MicroserviceContext(optionsBuilder.Options);
    }
}
