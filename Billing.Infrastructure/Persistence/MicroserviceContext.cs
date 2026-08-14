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

    public DbSet<Plan> Plans { get; set; }

    public DbSet<PlanVersion> PlanVersions { get; set; }

    public DbSet<PlanAssignment> PlanAssignments { get; set; }

    public DbSet<PlanAuditEntry> PlanAuditEntries { get; set; }

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

        modelBuilder.Entity<Plan>(planBuilder =>
        {
            planBuilder.Property(x => x.Name).IsRequired();
            planBuilder.Property(x => x.CreatedBy).IsRequired();

            // The name is the key everything else refers to - a grant names it, an assignment
            // resolves through it, and the configured catalogue is merged onto it by name - so two
            // plans sharing one would make "which numbers does this guild have" ambiguous in the
            // one place there is no way to ask.
            planBuilder.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<PlanVersion>(versionBuilder =>
        {
            versionBuilder.Property(x => x.ValuesJson).IsRequired();
            versionBuilder.Property(x => x.Reason).IsRequired();
            versionBuilder.Property(x => x.CreatedBy).IsRequired();

            versionBuilder.HasIndex(x => new { x.PlanId, x.VersionNumber }).IsUnique();

            // No navigation property in either direction, matching the rest of this context: the
            // constraint is what is wanted, and a collection on Plan would invite a query that loads
            // every version of every plan to answer a question about one.
            versionBuilder.HasOne<Plan>()
                .WithMany()
                .HasForeignKey(x => x.PlanId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PlanAssignment>(assignmentBuilder =>
        {
            assignmentBuilder.Property(x => x.SubjectKind).HasConversion<string>();
            assignmentBuilder.Property(x => x.SubjectId).IsRequired();
            assignmentBuilder.Property(x => x.AssignedBy).IsRequired();
            assignmentBuilder.Property(x => x.Reason).IsRequired();

            // One plan per subject, enforced rather than assumed: two rows would make a guild's
            // effective numbers depend on which one a query happened to read first.
            assignmentBuilder.HasIndex(x => new { x.SubjectKind, x.SubjectId }).IsUnique();

            // The blast radius query: how many subjects sit on each version of this plan.
            assignmentBuilder.HasIndex(x => new { x.PlanId, x.VersionNumber });

            assignmentBuilder.HasOne<Plan>()
                .WithMany()
                .HasForeignKey(x => x.PlanId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PlanAuditEntry>(auditBuilder =>
        {
            auditBuilder.Property(x => x.Action).HasConversion<string>();
            auditBuilder.Property(x => x.Actor).IsRequired();
            auditBuilder.Property(x => x.Reason).IsRequired();

            auditBuilder.HasIndex(x => new { x.PlanId, x.OccurredAt });

            auditBuilder.HasOne<Plan>()
                .WithMany()
                .HasForeignKey(x => x.PlanId)
                .OnDelete(DeleteBehavior.Restrict);
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
