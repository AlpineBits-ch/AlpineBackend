using Isle.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;

namespace Isle.Infrastructure.Persistence;

public class MicroserviceContext : DbContext
{
    public DbSet<Player> Players { get; set; }
}