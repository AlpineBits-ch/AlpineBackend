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
                table.HasCheckConstraint("ck_single_row_enforcer", "enforced_singleton = 1");
                
            });
            builder.HasIndex(x => x.EnforcedSingleton).IsUnique();
            
            builder.HasData(new EchoConfiguration()
            {
                Id = "ecco_3FQmtSXdg2VUCabuTR1r25imW2m",
                CreatedAt = new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero),
                UpdatedAt = new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero),
                IsLoginEnabled = true,
                IsRegisterEnabled = true,
            });
        });
        

    }

}