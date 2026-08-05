using AppEnvironment;
using Echo.Domain.Entities;
using Echo.Domain.Entities.Moderation;
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
