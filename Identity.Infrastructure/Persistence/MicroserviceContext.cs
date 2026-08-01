using AppEnvironment;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Persistence;

namespace Identity.Infrastructure.Persistence;

public class MicroserviceContext : IdentityDbContext<ApplicationUser, IdentityRole, string>
{
    public DbSet<UserPreferences> UserPreferences { get; set; }
    public DbSet<UserPublicKey> UserPublicKeys { get; set; }
    public DbSet<UserKey> UserKeys { get; set; }
    public DbSet<UserKeyPackage> UserKeyPackages { get; set; }
    public DbSet<UserDevice> UserDevices { get; set; }
    public DbSet<UserPushToken> UserPushTokens { get; set; }

    public DbSet<UserDeviceBackup> UserDeviceBackups { get; set; }
    public DbSet<UserBackupTransfer> UserBackupTransfers { get; set; }
    public DbSet<IdentityAuditEvent> IdentityAuditEvents { get; set; }
    public DbSet<LoginSession> LoginSessions { get; set; }
    
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