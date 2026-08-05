using AppEnvironment;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Domain;
using Identity.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Persistence;

namespace Identity.Infrastructure.Persistence;

public class MicroserviceContext : IdentityDbContext<ApplicationUser, IdentityRole, string>
{
    public DbSet<UserPreferences> UserPreferences { get; set; }
    public DbSet<UserPrivacySettings> UserPrivacySettings { get; set; }
    public DbSet<UserHiddenActivity> UserHiddenActivities { get; set; }
    public DbSet<UserPublicKey> UserPublicKeys { get; set; }
    public DbSet<UserKey> UserKeys { get; set; }
    public DbSet<UserKeyPackage> UserKeyPackages { get; set; }
    public DbSet<UserDevice> UserDevices { get; set; }
    public DbSet<UserPushToken> UserPushTokens { get; set; }

    public DbSet<UserDeviceBackup> UserDeviceBackups { get; set; }
    public DbSet<UserBackupTransfer> UserBackupTransfers { get; set; }
    public DbSet<IdentityAuditEvent> IdentityAuditEvents { get; set; }
    public DbSet<LoginSession> LoginSessions { get; set; }
    public DbSet<RevokedDeviceCertificate> RevokedDeviceCertificates { get; set; }
    public DbSet<LegalDocument> LegalDocuments { get; set; }
    public DbSet<UserConsent> UserConsents { get; set; }
    public DbSet<DataSubjectRequest> DataSubjectRequests { get; set; }
    public DbSet<DataExportRequest> DataExportRequests { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (optionsBuilder.IsConfigured) return;
        base.OnConfiguring(optionsBuilder);

        var env = Env.Database;
        optionsBuilder.UseNpgsql(env.ConnectionString(), options =>
        {
            options.MapEnum<AgeVertificationLevel>();
            options.MapEnum<Theme>();
            options.MapEnum<DirectMessageSettings>();
            options.MapEnum<PrivacySettings>();
            // UserPrivacySettings' four policy columns.
            options.MapEnum<DirectMessagePolicy>();
            options.MapEnum<FriendRequestPolicy>();
            options.MapEnum<Visibility>();
            options.MapEnum<ExplicitContentFilter>();
            options.MapEnum<DeviceStatus>();
            options.MapEnum<DeviceType>();
            options.MapEnum<PushTokenKind>();
            options.MapEnum<UserStatus>();
            options.MapEnum<UserType>();
        }).UseSnakeCaseNamingConvention();
        
       

    }
    public MicroserviceContext(DbContextOptions<MicroserviceContext> options) : base(options)
    {
        
    }
    
    private static string ConvertToSnakeCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        
        return System.Text.RegularExpressions.Regex
            .Replace(input, "([a-z0-9])([A-Z])", "$1_$2").ToLower();
    }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
        
            var tableName = entity.GetTableName();
            if (tableName != null)
            {
                entity.SetTableName(ConvertToSnakeCase(tableName));
            }
        }
        modelBuilder.Entity<ApplicationUser>(userBuilder =>
        {
            userBuilder.HasIndex(x => x.PhoneNumber).IsUnique();
            
            userBuilder.OwnsOne(x => x.AgeVerification, ageVerification =>
            {
                // nah no override
            });
            
            var userNameIndex = userBuilder.Metadata.GetIndexes()
                .FirstOrDefault(i => i.Properties.Any(p => p.Name == nameof(IdentityUser.NormalizedUserName)));
            userBuilder.Metadata.RemoveIndex(userNameIndex);
            userBuilder
                .HasIndex(u => u.NormalizedUserName)
                .HasDatabaseName("UserNameIndex")
                .IsUnique(false);
            userBuilder.HasIndex(x => x.Email).IsUnique();
            
            userBuilder.HasOne(u => u.UserPreferences)
                .WithOne()
                .HasForeignKey<ApplicationUser>(user => user.UserPreferencesId)
                .OnDelete(DeleteBehavior.Cascade);

            // Two wrappings of the same master key, stored side by side.
            userBuilder.OwnsOne(x => x.EncryptedMasterKey);
            userBuilder.OwnsOne(x => x.RecoveryCodeWrappedMasterKey);
        });

        // One row per account, in its own table, with the FK on the settings side - the mirror
        // image of UserPreferences, which hangs off ApplicationUser.UserPreferencesId.
        modelBuilder.Entity<UserPrivacySettings>(privacy =>
        {
            privacy.HasOne<ApplicationUser>()
                .WithOne(u => u.UserPrivacySettings)
                .HasForeignKey<UserPrivacySettings>(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Unique is implied by the 1:1, but stated so the intent survives a future change to
            // the relationship and so the backfill can rely on it.
            privacy.HasIndex(p => p.UserId).IsUnique();
        });

        modelBuilder.Entity<UserHiddenActivity>(hidden =>
        {
            hidden.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(h => h.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Read as a set, always for one user at a time.
            hidden.HasIndex(h => h.UserId);

            // Filtered uniques rather than one composite: the two keys are alternatives, and a
            // composite over two nullable columns would happily accept the same application twice
            // because Postgres treats each NULL as distinct.
            hidden.HasIndex(h => new { h.UserId, h.ApplicationId })
                .IsUnique()
                .HasFilter("application_id IS NOT NULL");

            hidden.HasIndex(h => new { h.UserId, h.Name })
                .IsUnique()
                .HasFilter("name IS NOT NULL");

            hidden.Property(h => h.ApplicationId).HasMaxLength(20);
            hidden.Property(h => h.Name).HasMaxLength(128);

            // The invariant the whole design rests on: one key or the other, never both, never
            // neither. A row with neither would match every activity a user has.
            hidden.ToTable(t => t.HasCheckConstraint(
                "ck_user_hidden_activities_exactly_one_key",
                "(application_id IS NULL) <> (name IS NULL)"));
        });

        modelBuilder.Entity<UserPublicKey>(key =>
        {
            key.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(k => k.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserKey>(key =>
        {
            key.HasOne<ApplicationUser>()
                .WithMany(u => u.UserKeys)
                .HasForeignKey(k => k.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        
        modelBuilder.Entity<UserPushToken>(token =>
        {
            token.HasOne<ApplicationUser>(t => t.User)
                .WithMany(u => u.PushTokens)
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Removing a device takes its push endpoints with it - that is the point of the link.
            token.HasOne(t => t.Device)
                .WithMany(d => d.PushTokens)
                .HasForeignKey(t => t.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);

            // One row per (transport, token).
            token.HasIndex(t => new { t.Kind, t.Token }).IsUnique();
            token.HasIndex(t => t.UserId);
        });


        modelBuilder.Entity<UserDeviceBackup>(backup =>
        {
            backup.HasOne<ApplicationUser>(b => b.User)
                .WithMany(u => u.Backups)
                .HasForeignKey(b => b.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // One-to-*many*, not one-to-one.
            backup.HasOne<UserDevice>(b => b.Device)
                .WithMany(d => d.Backups)
                .HasForeignKey(b => b.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);

            // The retention sweep and every read are "this device's versions, newest first".
            backup.HasIndex(b => new { b.DeviceId, b.Version }).IsUnique();
            backup.HasIndex(b => b.UserId);
        });

        modelBuilder.Entity<UserBackupTransfer>(transfer =>
        {
            transfer.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // The pending-transfers read is "what is waiting for this device", and the expiry sweep
            // walks the same rows.
            transfer.HasIndex(t => new { t.UserId, t.TargetDeviceId });
            transfer.HasIndex(t => t.ExpiresAt);
        });

        modelBuilder.Entity<IdentityAuditEvent>(audit =>
        {
            audit.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(a => a.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // The only read is "this account's recent security events, newest first".
            audit.HasIndex(a => new { a.UserId, a.CreatedAt });
        });

        modelBuilder.Entity<RevokedDeviceCertificate>(revocation =>
        {
            revocation.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(r => r.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Deliberately NOT keyed to user_devices: the whole point is that the row outlives the
            // device row it describes.
            revocation.HasIndex(r => new { r.UserId, r.CertificateFingerprint }).IsUnique();
            revocation.HasIndex(r => new { r.UserId, r.RevokedAt });
        });



        modelBuilder.Entity<UserKeyPackage>(keyPackage =>
        {
            keyPackage.HasOne(p => p.Device)
                .WithMany(d => d.KeyPackages)
                .HasForeignKey(p => p.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);  // keep this one

            keyPackage.HasOne(k => k.User)
                .WithMany(u => u.KeyPackages)
                .HasForeignKey(k => k.UserId)
                .OnDelete(DeleteBehavior.NoAction);  // ← change to NoAction
        });

        modelBuilder.Entity<UserDevice>(device =>
        {
            device.HasOne<ApplicationUser>(d => d.User)
                .WithMany(u => u.Devices)
                .HasForeignKey(d => d.UserId)  // ← missing
                .OnDelete(DeleteBehavior.Cascade);  // ← missing, delete user = delete devices
            device.Property(d => d.DeviceName).IsRequired();
            // Scoped to the user, not global.
            device.HasIndex(d => new { d.UserId, d.ClientDeviceId }).IsUnique();
        });

        modelBuilder.Entity<LoginSession>(session =>
        {
            session.HasOne(s => s.User)
                .WithMany()
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            // A removed device leaves its past logins in place (they are an audit trail) with the
            // link nulled out; the removal path revokes them explicitly.
            session.HasOne(s => s.Device)
                .WithMany()
                .HasForeignKey(s => s.DeviceId)
                .OnDelete(DeleteBehavior.SetNull);
            session.HasIndex(s => s.UserId);
        });

        // ── Legal documents, consent and the DSR queue (T1-10, T1-12, T1-13) ──
        modelBuilder.Entity<LegalDocument>(document =>
        {
            document.Property(d => d.DocumentType).HasConversion<string>().HasMaxLength(32);
            document.Property(d => d.Version).HasMaxLength(64);
            document.Property(d => d.ContentHash).HasMaxLength(64);

            // One row per published version.
            document.HasIndex(d => new { d.DocumentType, d.Version }).IsUnique();

            // "Which version is current" is the read on every registration and every self payload.
            document.HasIndex(d => new { d.DocumentType, d.EffectiveAt });
        });

        modelBuilder.Entity<UserConsent>(consent =>
        {
            consent.Property(c => c.DocumentType).HasConversion<string>().HasMaxLength(32);
            consent.Property(c => c.Version).HasMaxLength(64);

            consent.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Accepting the same version twice converges on one row rather than stacking duplicates -
            // a client that retries a POST whose response it never saw must not produce two records
            // of one decision.
            consent.HasIndex(c => new { c.UserId, c.DocumentType, c.Version }).IsUnique();
        });

        modelBuilder.Entity<DataSubjectRequest>(request =>
        {
            request.Property(r => r.Type).HasConversion<string>().HasMaxLength(32);
            request.Property(r => r.Status).HasConversion<string>().HasMaxLength(32);
            request.Property(r => r.Disposition).HasConversion<string>().HasMaxLength(32);
            request.Property(r => r.SubjectEmail).HasMaxLength(320);

            // Deliberately NOT an FK to asp_net_users.
            request.HasIndex(r => r.SubjectEmail);

            // The queue view is "open work, soonest deadline first", and the overdue banner is the
            // same query with a cutoff.
            request.HasIndex(r => new { r.Status, r.DueAt });
        });

        // T1-7. Same string-mapped-enum call as the three above, and for the same reason.
        modelBuilder.Entity<DataExportRequest>(export =>
        {
            export.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
            export.Property(e => e.ArtifactKey).HasMaxLength(512);
            export.Property(e => e.FailureReason).HasMaxLength(512);

            // text[], the same shape UserDevice.Capabilities uses.
            export.PrimitiveCollection(e => e.MissingServices);

            export.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Both reads this table has: the subject's own list, newest first, and the rate-limit
            // check, which is that same list bounded to the last 24 hours.
            export.HasIndex(e => new { e.UserId, e.RequestedAt });

            // The expiry sweep's read - "what is ready and past its window" - across all accounts.
            export.HasIndex(e => new { e.Status, e.ExpiresAt });
        });

        modelBuilder.UseOpenIddict();

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