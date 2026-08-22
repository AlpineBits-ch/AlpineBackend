using AppEnvironment;
using Discovery.Domain.Entities;
using Discovery.Domain.Topics;
using Microsoft.EntityFrameworkCore;
using Persistence;

namespace Discovery.Infrastructure.Persistence;

public class MicroserviceContext : DbContext
{
    public DbSet<Listing> Listings { get; set; }
    public DbSet<ListingTopic> ListingTopics { get; set; }
    public DbSet<Tag> Tags { get; set; }
    public DbSet<GameTopic> GameTopics { get; set; }
    public DbSet<UserInterest> UserInterests { get; set; }
    public DbSet<InterestVisibility> InterestVisibilities { get; set; }
    public DbSet<GuildProfile> GuildProfiles { get; set; }
    public DbSet<DiscoveryBan> DiscoveryBans { get; set; }

    public MicroserviceContext(DbContextOptions<MicroserviceContext> options) : base(options) { }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseNpgsql(Env.Database.ConnectionString()).UseSnakeCaseNamingConvention();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Listing>(listing =>
        {
            listing.HasIndex(l => l.GuildId).IsUnique();
            listing.HasIndex(l => new { l.State, l.LastBumpedAt });
            listing.Property(l => l.State).HasConversion<string>();
            listing.Property(l => l.JoinPolicy).HasConversion<string>();
            listing.Property(l => l.SuspendedReason).HasConversion<string>();
            listing.Property(l => l.Headline).HasMaxLength(80);
            listing.Property(l => l.Pitch).HasMaxLength(600);
            listing.Property(l => l.Language).HasMaxLength(16);
            listing.HasMany(l => l.Topics)
                .WithOne(t => t.Listing)
                .HasForeignKey(t => t.ListingId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ListingTopic>(topic =>
        {
            topic.Property(t => t.Kind).HasConversion<string>();
            topic.HasIndex(t => new { t.Kind, t.TopicId });
            topic.HasIndex(t => new { t.ListingId, t.Kind, t.TopicId }).IsUnique();
        });

        modelBuilder.Entity<Tag>(tag =>
        {
            tag.HasIndex(t => t.Slug).IsUnique();
            tag.Property(t => t.Slug).HasMaxLength(TagSlug.MaxLength);
            tag.Property(t => t.DisplayName).HasMaxLength(80);
        });

        modelBuilder.Entity<GameTopic>(game =>
        {
            game.HasIndex(g => g.GameApplicationId).IsUnique();
            game.Property(g => g.Name).HasMaxLength(200);
        });

        modelBuilder.Entity<UserInterest>(interest =>
        {
            interest.Property(i => i.Kind).HasConversion<string>();
            interest.Property(i => i.Source).HasConversion<string>();
            interest.HasIndex(i => i.UserId);
            interest.HasIndex(i => new { i.UserId, i.Kind, i.TopicId }).IsUnique();
        });

        modelBuilder.Entity<InterestVisibility>(v => v.HasIndex(x => x.UserId).IsUnique());

        modelBuilder.Entity<GuildProfile>(profile => profile.HasIndex(p => p.GuildId).IsUnique());

        modelBuilder.Entity<DiscoveryBan>(ban =>
        {
            // Not unique: a lifted ban keeps its row, and a guild can be banned again.
            ban.HasIndex(b => b.GuildId);
            ban.Property(b => b.Reason).HasMaxLength(500);
            ban.Property(b => b.StaffNote).HasMaxLength(1000);
        });
    }

    public override int SaveChanges()
    {
        ChangeTracker.UpdateTimestamps();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = new())
    {
        ChangeTracker.UpdateTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = new())
    {
        ChangeTracker.UpdateTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
