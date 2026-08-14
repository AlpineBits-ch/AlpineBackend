using AppEnvironment;
using Billing.Domain.Aggregates;
using Echo.Entitlements.Model;
using Microsoft.EntityFrameworkCore;
using Persistence;

namespace Billing.Infrastructure.Persistence;

public class MicroserviceContext : DbContext
{
    public DbSet<Grant> Grants { get; set; }

    public DbSet<EntitlementVersion> EntitlementVersions { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (optionsBuilder.IsConfigured)
            return;

        var env = Env.Database;

        // No MapEnum call, unlike Guild and Federation, and that is a decision rather than an
        // omission.
        optionsBuilder.UseNpgsql(env.ConnectionString()).UseSnakeCaseNamingConvention();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Grant>(grantBuilder =>
        {
            grantBuilder.Property(x => x.SubjectKind).HasConversion<string>();
            grantBuilder.Property(x => x.GrantKind).HasConversion<string>();
            grantBuilder.Property(x => x.Source).HasConversion<string>();

            grantBuilder.Property(x => x.Reason).IsRequired();
            grantBuilder.Property(x => x.CreatedBy).IsRequired();

            // The only query the resolver runs: every grant attached to this subject.
            grantBuilder.HasIndex(x => new { x.SubjectKind, x.SubjectId });

            // The expiry sweep's query.
            grantBuilder.HasIndex(x => x.ExpiresAt)
                .HasFilter("expires_at IS NOT NULL AND revoked_at IS NULL");
        });

        modelBuilder.Entity<EntitlementVersion>(versionBuilder =>
        {
            versionBuilder.Property(x => x.SubjectKind).HasConversion<string>();
            versionBuilder.Property(x => x.SubjectId).IsRequired();

            // Unique, and load-bearing rather than merely tidy: EntitlementVersionService advances
            // the counter with INSERT ...
            versionBuilder.HasIndex(x => new { x.SubjectKind, x.SubjectId }).IsUnique();
        });
    }

    public MicroserviceContext(DbContextOptions<MicroserviceContext> options) : base(options)
    {

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
