using AppEnvironment;
using Bots.Domain.Entity;
using Microsoft.EntityFrameworkCore;
using Persistence;

namespace Bots.Infrastructure.Persistence;

public class MicroserviceContext : DbContext
{
    public DbSet<BotApplication> BotApplications { get; set; }
    public DbSet<BotInstallation> BotInstallations { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (optionsBuilder.IsConfigured) return;

        var env = Env.Database;
        optionsBuilder.UseNpgsql(env.ConnectionString()).UseSnakeCaseNamingConvention();
    }

    public MicroserviceContext(DbContextOptions<MicroserviceContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<BotApplication>(app =>
        {
            app.HasIndex(x => x.OwnerUserId);
            app.HasIndex(x => x.BotUserId).IsUnique();
        });

        modelBuilder.Entity<BotInstallation>(install =>
        {
            install.HasIndex(x => new { x.BotApplicationId, x.GuildId }).IsUnique();
        });
    }

    public override int SaveChanges()
    {
        ChangeTracker.UpdateTimestamps();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = new CancellationToken())
    {
        ChangeTracker.UpdateTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = new CancellationToken())
    {
        ChangeTracker.UpdateTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
