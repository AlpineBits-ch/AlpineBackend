using AppEnvironment;
using Import.Domain.Entity;
using Microsoft.EntityFrameworkCore;
using Persistence;

namespace Import.Infrastructure.Persistence;

public class MicroserviceContext : DbContext
{
    public DbSet<ImportJob> ImportJobs { get; set; }
    public DbSet<GuildLink> GuildLinks { get; set; }
    public DbSet<ImportEntityMapping> ImportEntityMappings { get; set; }

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

        modelBuilder.Entity<ImportJob>(job =>
        {
            job.HasIndex(x => x.RequestedByUserId);
            job.HasIndex(x => x.DiscordGuildId);
        });

        modelBuilder.Entity<GuildLink>(link =>
        {
            link.HasIndex(x => x.EchoGuildId).IsUnique();
            link.HasIndex(x => x.DiscordGuildId).IsUnique();
        });

        modelBuilder.Entity<ImportEntityMapping>(mapping =>
        {
            mapping.HasIndex(x => new { x.GuildLinkId, x.DiscordId, x.EntityType }).IsUnique();
            mapping.HasIndex(x => new { x.GuildLinkId, x.EchoId, x.EntityType });
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
