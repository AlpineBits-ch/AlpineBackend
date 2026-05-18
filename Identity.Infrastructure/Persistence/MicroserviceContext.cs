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
    public DbSet<UserDeviceToken> UserDeviceTokens { get; set; }

    public DbSet<UserDeviceBackup> UserDeviceBackups { get; set; }
    
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
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
            options.MapEnum<UserStatus>();
        }).UseSnakeCaseNamingConvention();
        
       

    }
    public MicroserviceContext(DbContextOptions<MicroserviceContext> options) : base(options)
    {
        
    }
    
    private string ConvertToSnakeCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
    
        // Simple regex or manual logic to convert PascalCase to snake_case
        // Note: EFCore.NamingConventions usually has a utility for this, 
        // but doing it explicitly here ensures Identity tables follow suit.
        return System.Text.RegularExpressions.Regex
            .Replace(input, "([a-z0-9])([A-Z])", "$1_$2").ToLower();
    }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            // Replace "AspNet" prefix if you want (e.g., AspNetUsers -> users)
            // Or just let the convention handle the transformation
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

            userBuilder.OwnsOne(x => x.EncryptedMasterKey);
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
        
        modelBuilder.Entity<UserDeviceToken>(token =>
        {
            token.HasOne<ApplicationUser>(t => t.User)
                .WithMany(u => u.DeviceTokens)
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });


        modelBuilder.Entity<UserDeviceBackup>(backup =>
        {
            backup.HasOne<ApplicationUser>(b => b.User)
                .WithMany(u => u.Backups)
                .HasForeignKey(b => b.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            backup.HasOne<UserDevice>(b => b.Device)
                .WithOne(d => d.Backup)
                .HasForeignKey<UserDeviceBackup>(b => b.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
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
            device.HasIndex(d => d.ClientDeviceId).IsUnique();
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