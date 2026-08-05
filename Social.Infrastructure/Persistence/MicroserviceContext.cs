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
    public DbSet<GameApplication> GameApplications { get; set; }
    public DbSet<GameExecutable> GameExecutables { get; set; }
    public DbSet<GameCatalogState> GameCatalogStates { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var env = Env.Database;

        optionsBuilder.UseNpgsql(env.ConnectionString(), options =>
        {
            options.MapEnum<RelationshipStatus>();
            options.MapEnum<OnlineStatus>();
            options.MapEnum<ProfileFont>();
            options.MapEnum<GameCatalogSource>();
            options.MapEnum<GamePlatform>();
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
            // GetBlockRelationshipsRequest asks "is there a Blocked row from A to B".
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

        modelBuilder.Entity<GameApplication>(gameBuilder =>
        {
            // Unique and filtered: this is the key that turns an application id arriving over a
            // local socket into a name we are willing to broadcast, so two rows claiming the same
            // id would make that resolution ambiguous.
            gameBuilder.HasIndex(g => g.DiscordApplicationId)
                .IsUnique()
                .HasFilter("discord_application_id IS NOT NULL");

            // Name search for the per-game privacy list and for community submissions checking
            // whether a game is already catalogued.
            gameBuilder.HasIndex(g => g.Name);

            gameBuilder.Property(g => g.Name).HasMaxLength(256);
            gameBuilder.Property(g => g.SteamAppId).HasMaxLength(32);
        });

        modelBuilder.Entity<GameExecutable>(execBuilder =>
        {
            // The detection hot path: narrow by (platform, basename) on an index seek, then verify
            // the full path-suffix rule in memory.
            execBuilder.HasIndex(e => new { e.Os, e.Basename });

            execBuilder.Property(e => e.Name).HasMaxLength(512);
            execBuilder.Property(e => e.Basename).HasMaxLength(256);

            execBuilder.HasOne(e => e.GameApplication)
                .WithMany(g => g.Executables)
                .HasForeignKey(e => e.GameApplicationId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);
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