using AppEnvironment;
using Federation.Domain.Aggregates;
using Federation.Domain.Events;
using Microsoft.EntityFrameworkCore;

namespace Federation.Infrastructure.Persistence;

public class MicroserviceContext : DbContext
{
    public DbSet<FederationInstance> FederationInstances { get; set; }
    
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
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
    }
    
    public MicroserviceContext(DbContextOptions<MicroserviceContext> options) : base(options)
    {
        
    }
}