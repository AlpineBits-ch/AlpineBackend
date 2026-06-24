using AppEnvironment;
using Federation.Domain.Aggregates;
using Federation.Domain.Events;
using Microsoft.EntityFrameworkCore;

namespace Federation.Infrastructure.Persistence;

public class MicroserviceContext : DbContext
{
    public DbSet<FederationInstance> FederationInstances { get; set; }
    public DbSet<FederatedEventRecord> FederatedEvents { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (optionsBuilder.IsConfigured)
            return;

        var env = Env.Database;
        optionsBuilder.UseNpgsql(env.ConnectionString(), options =>
        {
            options.MapEnum<FederationStatus>();
        }).UseSnakeCaseNamingConvention();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FederationInstance>(builder =>
        {
            
        });

        modelBuilder.Entity<FederatedGuild>(builder =>
        {
            builder.HasOne(x => x.Instance)
                .WithMany(x => x.FederatedGuilds)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FederatedEventRecord>(builder =>
        {
            builder.HasKey(e => e.EventId);
        });
    }
    
    public MicroserviceContext(DbContextOptions<MicroserviceContext> options) : base(options)
    {
        
    }
}