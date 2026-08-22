using AppEnvironment;
using Microsoft.EntityFrameworkCore;
using Persistence;

namespace Discovery.Infrastructure.Persistence;

public class MicroserviceContext : DbContext
{
    public MicroserviceContext(DbContextOptions<MicroserviceContext> options) : base(options) { }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseNpgsql(Env.Database.ConnectionString()).UseSnakeCaseNamingConvention();
    }

    public override int SaveChanges()
    {
        ChangeTracker.UpdateTimestamps();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = new())
    {
        ChangeTracker.UpdateTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = new())
    {
        ChangeTracker.UpdateTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
