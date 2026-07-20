using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Isle.Infrastructure
{
    public class DesignTimeFactory : IDesignTimeDbContextFactory<MicroserviceContext>
    {
        public MicroserviceContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<MicroserviceContext>();
            
            // Use a dummy connection string.

            return new MicroserviceContext(optionsBuilder.Options);
        }   
    }
}