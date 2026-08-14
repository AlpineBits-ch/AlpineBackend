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

    public DbSet<StripeCustomer> StripeCustomers { get; set; }

    public DbSet<Subscription> Subscriptions { get; set; }

    public DbSet<ProcessedStripeEvent> ProcessedStripeEvents { get; set; }

    public DbSet<CreditEntry> CreditEntries { get; set; }

    public DbSet<CreditLot> CreditLots { get; set; }

    public DbSet<CreditWallet> CreditWallets { get; set; }

    public DbSet<CreditCampaign> CreditCampaigns { get; set; }

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

        modelBuilder.Entity<StripeCustomer>(customerBuilder =>
        {
            customerBuilder.Property(x => x.UserId).IsRequired();
            customerBuilder.Property(x => x.StripeCustomerId).IsRequired();

            // Unique in both directions.
            customerBuilder.HasIndex(x => x.UserId).IsUnique();
            customerBuilder.HasIndex(x => x.StripeCustomerId).IsUnique();
        });

        modelBuilder.Entity<Subscription>(subscriptionBuilder =>
        {
            subscriptionBuilder.Property(x => x.SubjectKind).HasConversion<string>();
            subscriptionBuilder.Property(x => x.Status).HasConversion<string>();

            subscriptionBuilder.Property(x => x.StripeSubscriptionId).IsRequired();
            subscriptionBuilder.Property(x => x.PayerUserId).IsRequired();
            subscriptionBuilder.Property(x => x.SubjectId).IsRequired();

            subscriptionBuilder.HasIndex(x => x.StripeSubscriptionId).IsUnique();

            // The one that stops a double charge.
            subscriptionBuilder.HasIndex(x => new { x.SubjectKind, x.SubjectId })
                .IsUnique()
                .HasFilter("status IN ('Trialing', 'Active', 'PastDue')");

            // The webhook's other entry point: given a price, which plan version was bought.
            subscriptionBuilder.HasOne<Plan>()
                .WithMany()
                .HasForeignKey(x => x.PlanId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProcessedStripeEvent>(eventBuilder =>
        {
            // Stripe's own event id, and the reason this table exists: the insert is the duplicate
            // check, so the constraint has to be the primary key rather than a unique index beside a
            // generated one. See the class comment.
            eventBuilder.HasKey(x => x.EventId);

            eventBuilder.Property(x => x.EventId).IsRequired();
            eventBuilder.Property(x => x.Type).IsRequired();
        });

        modelBuilder.Entity<CreditEntry>(entryBuilder =>
        {
            entryBuilder.Property(x => x.Kind).HasConversion<string>();
            entryBuilder.Property(x => x.UserId).IsRequired();
            entryBuilder.Property(x => x.IdempotencyKey).IsRequired();

            // The one constraint the concurrency story rests on.
            entryBuilder.HasIndex(x => x.IdempotencyKey).IsUnique();

            // The ledger read, and the balance rebuild.
            entryBuilder.HasIndex(x => new { x.UserId, x.CreatedAt });

            // Lot remainders: every entry that drew a lot down, which is how "what is left in this
            // lot" is answered without a counter to get out of step.
            entryBuilder.HasIndex(x => x.LotId);

            // Per-campaign, per-recipient caps.
            entryBuilder.HasIndex(x => new { x.CampaignId, x.UserId })
                .HasFilter("campaign_id IS NOT NULL");

            entryBuilder.ToTable(table =>
            {
                // Required on the two kinds a human chose to write.
                table.HasCheckConstraint(
                    "ck_credit_entries_reason_required",
                    "kind NOT IN ('Adjustment', 'Reversal') OR (reason IS NOT NULL AND btrim(reason) <> '')");

                // The sign belongs to the kind, so a spend that credited somebody cannot be written
                // at all. Without this the ledger's arithmetic is only as good as the last caller.
                table.HasCheckConstraint(
                    "ck_credit_entries_amount_sign",
                    "(kind = 'Issue' AND amount > 0) "
                    + "OR (kind IN ('Spend', 'Expiry', 'Reversal') AND amount < 0) "
                    + "OR (kind = 'Adjustment' AND amount <> 0)");
            });
        });

        modelBuilder.Entity<CreditLot>(lotBuilder =>
        {
            lotBuilder.Property(x => x.UserId).IsRequired();

            // Earliest-expiring first, which is the order every spend walks.
            lotBuilder.HasIndex(x => new { x.UserId, x.ExpiresAt });

            // The two sweeps: what has lapsed, and what is about to and has not been warned about.
            lotBuilder.HasIndex(x => x.ExpiresAt);
            lotBuilder.HasIndex(x => new { x.ExpiresAt, x.ExpiryWarningSentAt })
                .HasFilter("expiry_warning_sent_at IS NULL");

            lotBuilder.ToTable(table => table.HasCheckConstraint(
                "ck_credit_lots_amount_positive", "original_amount > 0"));
        });

        modelBuilder.Entity<CreditWallet>(walletBuilder =>
        {
            walletBuilder.Property(x => x.UserId).IsRequired();

            // Unique, and load-bearing for the same reason the entitlement version's index is: the
            // spend takes its row lock with INSERT ...
            walletBuilder.HasIndex(x => x.UserId).IsUnique();

            // "Never negative" (section 8.5), said to the database as well as to the service.
            walletBuilder.ToTable(table => table.HasCheckConstraint(
                "ck_credit_wallets_balance_not_negative", "cached_balance >= 0"));
        });

        modelBuilder.Entity<CreditCampaign>(campaignBuilder =>
        {
            campaignBuilder.Property(x => x.Code).IsRequired();
            campaignBuilder.Property(x => x.Description).IsRequired();
            campaignBuilder.Property(x => x.CreatedBy).IsRequired();

            campaignBuilder.HasIndex(x => x.Code).IsUnique();

            // The budget, in the database.
            campaignBuilder.ToTable(table => table.HasCheckConstraint(
                "ck_credit_campaigns_within_budget",
                "total_budget_points > 0 AND issued_points >= 0 AND issued_points <= total_budget_points"));
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
