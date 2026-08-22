using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Discovery.Infrastructure.Persistence;

public class DesignTimeFactory : IDesignTimeDbContextFactory<MicroserviceContext>
{
    public MicroserviceContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<MicroserviceContext>().Options);
}
