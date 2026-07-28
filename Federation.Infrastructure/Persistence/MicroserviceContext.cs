using AppEnvironment;
using Federation.Domain.Aggregates;
using Federation.Domain.Events;
using Microsoft.EntityFrameworkCore;

namespace Federation.Infrastructure.Persistence;

public class MicroserviceContext : DbContext
{
    public DbSet<FederationInstance> FederationInstances { get; set; }
    public DbSet<FederatedResource> FederatedResources { get; set; }
    public DbSet<FederatedEventRecord> FederatedEvents { get; set; }
    public DbSet<FederationSettings> FederationSettings { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (optionsBuilder.IsConfigured)
            return;

        var env = Env.Database;
        optionsBuilder.UseNpgsql(env.ConnectionString(), options =>
        {
            options.MapEnum<FederationStatus>();
            options.MapEnum<AcceptancePolicy>();
            options.MapEnum<FederatedResourceType>();
        }).UseSnakeCaseNamingConvention();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FederationInstance>(builder =>
        {
            // Nothing today prevented FederationHandshakeEndpoint (inbound) and
            // AdminFederationEndpoint.InitiateHandshakeAsync (outbound) from racing to create two
            // rows for the same host - see the federation split-brain plan.
            builder.HasIndex(x => x.Host).IsUnique();
        });

        modelBuilder.Entity<FederatedResource>(builder =>
        {
            builder.HasOne(x => x.Instance)
                .WithMany(x => x.FederatedResources)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasIndex(x => new { x.ResourceType, x.LocalId });
            builder.HasIndex(x => new { x.InstanceId, x.ResourceType, x.RemoteId }).IsUnique();
        });

        modelBuilder.Entity<FederatedEventRecord>(builder =>
        {
            builder.HasKey(e => e.EventId);
        });

        modelBuilder.Entity<FederationSettings>(builder =>
        {
            builder.HasKey(s => s.Id);
        });
    }
    
    public MicroserviceContext(DbContextOptions<MicroserviceContext> options) : base(options)
    {
        
    }
}