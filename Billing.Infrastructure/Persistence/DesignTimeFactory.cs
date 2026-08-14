using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Billing.Infrastructure.Persistence;

public class DesignTimeFactory : IDesignTimeDbContextFactory<MicroserviceContext>
{
    public MicroserviceContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<MicroserviceContext>();

        // Deliberately unconfigured.

        return new MicroserviceContext(optionsBuilder.Options);
    }
}
