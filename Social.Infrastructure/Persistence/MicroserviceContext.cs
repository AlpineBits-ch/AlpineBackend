using AppEnvironment;
using Microsoft.EntityFrameworkCore;
using Persistence;
using Social.Domain.Aggregate;
using Social.Domain.Enums;

namespace Social.Infrastructure.Persistence;

public class MicroserviceContext : DbContext
{
    public DbSet<Profile> Profiles { get; set; }
    public DbSet<Relationship> Relationships { get; set; }
   
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var env = Env.Database;

        optionsBuilder.UseNpgsql(env.ConnectionString(), options =>
        {
            options.MapEnum<RelationshipStatus>();
            options.MapEnum<OnlineStatus>();
        }).UseSnakeCaseNamingConvention();
    }
    
    public MicroserviceContext(DbContextOptions<MicroserviceContext> options) : base(options)
    {
        
    }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {

        modelBuilder.Entity<Profile>(profileBuilder =>
        {
            profileBuilder.HasIndex(x => x.UserId).IsUnique();
            profileBuilder.HasIndex(x => x.UserName);
            profileBuilder.HasIndex(x => x.Hash);
        });

        modelBuilder.Entity<Relationship>(relationshipBuilder =>
        {
            relationshipBuilder.HasOne(r => r.Owner)
                .WithMany(p => p.Relationships)
                .HasForeignKey(r => r.OwnerId)
                .IsRequired()                
                .OnDelete(DeleteBehavior.Cascade);

            
            relationshipBuilder.HasOne(r => r.Target)
                .WithMany(p => p.InvolvedRelationships)
                .HasForeignKey(r => r.TargetId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);
            
            relationshipBuilder.HasOne(r => r.Related)
                .WithOne()
                .HasForeignKey<Relationship>("RelatedId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired(false);
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