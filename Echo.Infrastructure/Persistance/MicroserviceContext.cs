using System.Runtime.CompilerServices;
using AppEnvironment;
using Echo.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Echo.Persistence.Persistance;

public class MicroserviceContext : DbContext
{
    public DbSet<EchoConfiguration> EchoConfigurations { get; set; }
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var env = Env.Database;

        optionsBuilder.UseNpgsql(env.ConnectionString(), options =>
        {
         
        }).UseSnakeCaseNamingConvention();
    }
    
    public MicroserviceContext(DbContextOptions<MicroserviceContext> options) : base(options)
    {
        
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);


        modelBuilder.Entity<EchoConfiguration>(builder =>
        {
            builder.Property(x => x.EnforcedSingleton)
                .HasDefaultValue(1);
            builder.ToTable(table =>
            {
                table.HasCheckConstraint("ck_single_row_enforcer", "[enforced_singleton] = 1");
            });
            builder.HasIndex(x => x.EnforcedSingleton).IsUnique();
        });

    }

}