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
            options.MapEnum<ProfileFont>();
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
            profileBuilder.HasIndex(x => x.UserName).IsUnique();
        });

        modelBuilder.Entity<Relationship>(relationshipBuilder =>
        {
            // Blocking (privacy spec T0-3) turned the directed (owner, target) pair into a hot
            // point lookup: every friend request, every profile read and every cross-service
            // GetBlockRelationshipsRequest asks "is there a Blocked row from A to B". The two
            // single-column indexes that already exist would each hand back the whole of one
            // user's social graph for the engine to filter.
            relationshipBuilder.HasIndex(r => new { r.OwnerId, r.TargetId });

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