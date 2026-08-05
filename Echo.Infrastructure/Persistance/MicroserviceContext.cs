using AppEnvironment;
using Echo.Domain.Entities;
using Echo.Domain.Entities.Moderation;
using Echo.Domain.Entities.Status;
using Microsoft.EntityFrameworkCore;

namespace Echo.Persistence.Persistance;

public class MicroserviceContext : DbContext
{
    public DbSet<EchoConfiguration> EchoConfigurations { get; set; }

    // Moderation and support.
    public DbSet<ModerationReport> ModerationReports { get; set; }
    public DbSet<ModerationAction> ModerationActions { get; set; }
    public DbSet<ModerationAppeal> ModerationAppeals { get; set; }
    public DbSet<SupportTicket> SupportTickets { get; set; }
    public DbSet<SupportTicketMessage> SupportTicketMessages { get; set; }
    public DbSet<ModerationAuditEntry> ModerationAuditEntries { get; set; }

    // Status page and incidents, here for the same reason and one more: the gateway is the only
    // process that already sees every request and every response, and already health-checks every
    // backend.
    public DbSet<StatusComponent> StatusComponents { get; set; }
    public DbSet<StatusIncident> StatusIncidents { get; set; }
    public DbSet<StatusIncidentUpdate> StatusIncidentUpdates { get; set; }
    public DbSet<StatusIncidentComponent> StatusIncidentComponents { get; set; }
    public DbSet<StatusDayRollup> StatusDayRollups { get; set; }

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

        ConfigureModeration(modelBuilder);
        ConfigureStatus(modelBuilder);
    }

    /// <summary>The status page's tables.</summary>
    private static void ConfigureStatus(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StatusComponent>(builder =>
        {
            builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);

            builder.Property(x => x.Key).HasMaxLength(32).IsRequired();
            builder.Property(x => x.Name).HasMaxLength(StatusComponent.MaxNameLength).IsRequired();
            builder.Property(x => x.Description).HasMaxLength(StatusComponent.MaxDescriptionLength);
            builder.Property(x => x.ImpactHint).HasMaxLength(StatusComponent.MaxImpactHintLength);

            // The seed matches on Key, so a rename in the console has to survive every subsequent
            // startup - which it only does if two rows can never share a key.
            builder.HasIndex(x => x.Key).IsUnique();
            builder.HasIndex(x => x.Position);
        });

        modelBuilder.Entity<StatusIncident>(builder =>
        {
            builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            builder.Property(x => x.Impact).HasConversion<string>().HasMaxLength(32);
            builder.Property(x => x.Origin).HasConversion<string>().HasMaxLength(32);

            builder.Property(x => x.Title).HasMaxLength(StatusIncident.MaxTitleLength).IsRequired();
            builder.Property(x => x.Template).HasMaxLength(32);
            builder.Property(x => x.DetectionDetail).HasMaxLength(StatusIncident.MaxDetectionDetailLength);

            builder.Property(x => x.Reference)
                .HasMaxLength(PublicReference.TotalLength)
                .IsRequired();

            builder.HasIndex(x => x.Reference).IsUnique();

            // The public page's own query: not retracted, newest first, open ones first.
            builder.HasIndex(x => new { x.IsRetracted, x.ResolvedAt, x.StartedAt });

            // The cross-replica dedupe, and the only reason the detector is safe to run on more
            // than one gateway at once.
            builder.HasIndex(x => x.AutoComponentId)
                .IsUnique()
                .HasFilter("resolved_at IS NULL AND origin = 'Automatic' AND is_retracted = false")
                .HasDatabaseName("ix_status_incidents_auto_open");
        });

        modelBuilder.Entity<StatusIncidentUpdate>(builder =>
        {
            builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            builder.Property(x => x.Body).HasMaxLength(StatusIncidentUpdate.MaxBodyLength);
            builder.Property(x => x.Template).HasMaxLength(32);

            builder.HasIndex(x => new { x.IncidentId, x.PostedAt });

            builder.HasOne(x => x.Incident)
                .WithMany(x => x.Updates)
                .HasForeignKey(x => x.IncidentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StatusIncidentComponent>(builder =>
        {
            builder.HasKey(x => new { x.IncidentId, x.ComponentId });

            builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);

            builder.HasOne(x => x.Incident)
                .WithMany(x => x.Components)
                .HasForeignKey(x => x.IncidentId)
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict, not cascade: deleting a component that an incident refers to would rewrite
            // history to say the outage affected less than it did.
            builder.HasOne(x => x.Component)
                .WithMany()
                .HasForeignKey(x => x.ComponentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StatusDayRollup>(builder =>
        {
            builder.Property(x => x.Day).HasColumnType("date");

            // One row per component per day, enforced here because the probe upserts it from every
            // replica on every tick.
            builder.HasIndex(x => new { x.ComponentId, x.Day }).IsUnique();

            builder.HasOne(x => x.Component)
                .WithMany()
                .HasForeignKey(x => x.ComponentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>Enum columns are stored as strings throughout, not as ints.</summary>
    private static void ConfigureModeration(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ModerationReport>(builder =>
        {
            builder.Property(x => x.SubjectKind).HasConversion<string>().HasMaxLength(32);
            builder.Property(x => x.Reason).HasConversion<string>().HasMaxLength(32);
            builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            builder.Property(x => x.Priority).HasConversion<string>().HasMaxLength(32);

            builder.Property(x => x.Details).HasMaxLength(ModerationReport.MaxDetailsLength);
            builder.Property(x => x.EvidenceJson).HasMaxLength(ModerationReport.MaxEvidenceLength);
            builder.Property(x => x.Resolution).HasMaxLength(2000);

            builder.HasIndex(x => x.TargetUserId);
            builder.HasIndex(x => x.ReporterUserId);
            builder.HasIndex(x => x.AssignedToUserId);

            // The queue's own sort: open work first, worst first, oldest first.
            builder.HasIndex(x => new { x.Status, x.Priority, x.CreatedAt });

            // Answers the duplicate guard on POST /api/v1/reports - "has this reporter already
            // reported this subject recently" - without scanning the reporter's whole history.
            builder.HasIndex(x => new { x.ReporterUserId, x.SubjectKind, x.SubjectId, x.CreatedAt });
        });

        modelBuilder.Entity<ModerationAction>(builder =>
        {
            builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            builder.Property(x => x.Reason).HasConversion<string>().HasMaxLength(32);

            builder.Property(x => x.PublicNote).HasMaxLength(ModerationAction.MaxNoteLength);
            builder.Property(x => x.InternalNote).HasMaxLength(ModerationAction.MaxNoteLength);
            builder.Property(x => x.RevocationReason).HasMaxLength(ModerationAction.MaxNoteLength);

            builder.Property(x => x.Reference)
                .HasMaxLength(PublicReference.TotalLength)
                .IsRequired();

            // Unique because it is the lookup key on the anonymous appeal form.
            builder.HasIndex(x => x.Reference).IsUnique();

            builder.HasIndex(x => x.TargetUserId);
            builder.HasIndex(x => x.ActorUserId);

            // "Is this account currently sanctioned" - the query the console runs for every row of
            // every user list.
            builder.HasIndex(x => new { x.TargetUserId, x.Kind, x.RevokedAt, x.ExpiresAt });
        });

        modelBuilder.Entity<ModerationAppeal>(builder =>
        {
            builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            builder.Property(x => x.Body).HasMaxLength(ModerationAppeal.MaxBodyLength);
            builder.Property(x => x.DecisionNote).HasMaxLength(ModerationAppeal.MaxBodyLength);
            builder.Property(x => x.ContactEmail).HasMaxLength(320).IsRequired();

            builder.Property(x => x.Reference)
                .HasMaxLength(PublicReference.TotalLength)
                .IsRequired();

            builder.HasIndex(x => x.Reference).IsUnique();

            // One appeal per action, enforced here rather than by a read-then-write in the
            // controller: the appeal form is anonymous and rate-limited per address, so two
            // submissions racing is a thing that happens rather than a thing that might.
            builder.HasIndex(x => x.ActionId).IsUnique();

            builder.HasIndex(x => new { x.Status, x.CreatedAt });

            builder.HasOne(x => x.Action)
                .WithMany(x => x.Appeals)
                .HasForeignKey(x => x.ActionId)
                // The action is the entire subject of the appeal; an appeal outliving it would be a
                // row nothing can render. Nothing in the application deletes actions anyway.
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SupportTicket>(builder =>
        {
            builder.Property(x => x.Category).HasConversion<string>().HasMaxLength(32);
            builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);

            builder.Property(x => x.ContactEmail).HasMaxLength(320).IsRequired();
            builder.Property(x => x.Subject).HasMaxLength(SupportTicket.MaxSubjectLength).IsRequired();

            builder.Property(x => x.Reference)
                .HasMaxLength(PublicReference.TotalLength)
                .IsRequired();

            builder.HasIndex(x => x.Reference).IsUnique();
            builder.HasIndex(x => x.ContactEmail);
            builder.HasIndex(x => x.AssignedToUserId);
            builder.HasIndex(x => new { x.Status, x.LastActivityAt });
        });

        modelBuilder.Entity<SupportTicketMessage>(builder =>
        {
            builder.Property(x => x.AuthorKind).HasConversion<string>().HasMaxLength(32);
            builder.Property(x => x.Body).HasMaxLength(SupportTicketMessage.MaxBodyLength);

            builder.HasIndex(x => new { x.TicketId, x.CreatedAt });

            builder.HasOne(x => x.Ticket)
                .WithMany(x => x.Messages)
                .HasForeignKey(x => x.TicketId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ModerationAuditEntry>(builder =>
        {
            builder.Property(x => x.Action).HasMaxLength(64).IsRequired();
            builder.Property(x => x.ActorUserId).HasMaxLength(64).IsRequired();
            builder.Property(x => x.Detail).HasMaxLength(1000);
            builder.Property(x => x.IpAddress).HasMaxLength(64);

            builder.HasIndex(x => new { x.ActorUserId, x.CreatedAt });
            builder.HasIndex(x => new { x.SubjectId, x.CreatedAt });
        });
    }
}
