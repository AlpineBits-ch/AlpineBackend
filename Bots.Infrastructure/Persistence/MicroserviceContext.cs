using AppEnvironment;
using Bots.Domain.Entity;
using Microsoft.EntityFrameworkCore;
using Persistence;

namespace Bots.Infrastructure.Persistence;

public class MicroserviceContext : DbContext
{
    public DbSet<BotApplication> BotApplications { get; set; }
    public DbSet<BotInstallation> BotInstallations { get; set; }
    public DbSet<BotCommand> BotCommands { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (optionsBuilder.IsConfigured) return;

        var env = Env.Database;
        optionsBuilder.UseNpgsql(env.ConnectionString()).UseSnakeCaseNamingConvention();
    }

    public MicroserviceContext(DbContextOptions<MicroserviceContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<BotApplication>(app =>
        {
            app.HasIndex(x => x.OwnerUserId);
            app.HasIndex(x => x.BotUserId).IsUnique();
        });

        modelBuilder.Entity<BotInstallation>(install =>
        {
            install.HasIndex(x => new { x.BotApplicationId, x.GuildId }).IsUnique();
        });

        // Postgres treats every NULL as distinct in a unique index, so a single
        // (BotApplicationId, GuildId, Name) index would never actually enforce uniqueness among
        // global commands (GuildId == null) - split into two filtered indexes instead.
        modelBuilder.Entity<BotCommand>(command =>
        {
            command.HasIndex(x => new { x.BotApplicationId, x.Name })
                .HasDatabaseName("IX_bot_commands_global_unique")
                .IsUnique()
                .HasFilter("guild_id IS NULL");

            command.HasIndex(x => new { x.BotApplicationId, x.GuildId, x.Name })
                .HasDatabaseName("IX_bot_commands_guild_unique")
                .IsUnique()
                .HasFilter("guild_id IS NOT NULL");
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
