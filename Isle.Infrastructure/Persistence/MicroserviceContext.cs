using System.Numerics;
using System.Text.Json;
using AppEnvironment;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity;
using Isle.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Persistence;

namespace Isle.Infrastructure.Persistence;

public class MicroserviceContext : DbContext
{
    public DbSet<Player> Players { get; set; }
    public DbSet<Storage> Storages { get; set; }
    public DbSet<StorageSlot> StorageSlots { get; set; }
    public DbSet<PlayerInvite> PlayerInvites { get; set; }
    public DbSet<Skin> Skins { get; set; }
    public DbSet<GameModeRun> GameModeRuns { get; set; }
    
    public DbSet<GameModeDefinition> GameModeDefinitions { get; set; }
    
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var env = Env.Database;

        optionsBuilder.UseNpgsql(env.ConnectionString(), options =>
        {
            options.MapEnum<GameModeType>();
            options.MapEnum<GeoFenceShape>();
            options.MapEnum<TriggerType>();
            options.MapEnum<RankRequirement>();
            options.MapEnum<GameModeState>();
        }).UseSnakeCaseNamingConvention();
    }
    public MicroserviceContext(DbContextOptions<MicroserviceContext> options) : base(options)
    {
        
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {

        modelBuilder.Entity<GameModeRun>(runBuilder =>
        {
            runBuilder.HasOne(run => run.Definition)
                .WithMany(def => def.Runs)
                .HasForeignKey(run => run.DefinitionId);
            
            runBuilder.OwnsMany(run => run.Results);
            
        });
        
        
        modelBuilder.Entity<GameModeDefinition>(gameModeDefinitionBuilder =>
        {
            gameModeDefinitionBuilder.HasKey(mode => mode.Id);

            gameModeDefinitionBuilder.Property(mode => mode.Type)
                .IsRequired();

            gameModeDefinitionBuilder.Property(mode => mode.DisplayName)
                .IsRequired();

            gameModeDefinitionBuilder.OwnsOne(mode => mode.Zone, zone =>
            {
                zone.Property(z => z.Shape);

                zone.Property(z => z.Center)
                    .HasConversion(
                        v => $"{v.X},{v.Y},{v.Z}",
                        v => ParseVector3(v));

                zone.Property(z => z.Radius);

                zone.Property(z => z.PolygonPoints)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                        v => JsonSerializer.Deserialize<List<Vector3>>(v, (JsonSerializerOptions?)null) ?? new List<Vector3>());
            });

            gameModeDefinitionBuilder.OwnsOne(mode => mode.Trigger, trigger =>
            {
                trigger.Property(t => t.Type);
                trigger.Property(t => t.Interval);
                trigger.Property(t => t.MinPlayersToTrigger);
            });

           
            gameModeDefinitionBuilder.OwnsMany(mode => mode.Rewards, reward =>
            {
                reward.Property(r => r.RewardType);
                reward.Property(r => r.Amount);
                reward.Property(r => r.CosmeticId);
                reward.Property(r => r.AppliesTo);
            });
        });

        
        
        
        modelBuilder.Entity<Player>(playerBuilder =>
        {
            modelBuilder.HasSequence<long>("player_friendly_id_seq")
                .StartsAt(100000)
                .IncrementsBy(1);

            modelBuilder.Entity<Player>()
                .Property(p => p.FriendlyIdSeq)
                .HasDefaultValueSql("nextval('player_friendly_id_seq')");
        });


        modelBuilder.Entity<Skin>(skinBuilder =>
        {
            skinBuilder.HasOne(s => s.Player)
                .WithMany(p => p.Skins)
                .HasForeignKey(s => s.PlayerId);
            
            skinBuilder.OwnsOne(s => s.Customizer, customizerBuilder =>
            {
                
                
                customizerBuilder.OwnsOne(c => c.BodyColor);
                customizerBuilder.OwnsOne(c => c.MarkingsColor);
                customizerBuilder.OwnsOne(c => c.FlankColor);
                customizerBuilder.OwnsOne(c => c.UnderbellyColor);
                customizerBuilder.OwnsOne(c => c.Detail1Color);
                customizerBuilder.OwnsOne(c => c.EyesColor);
                customizerBuilder.OwnsOne(c => c.MaleDisplayColor);

                customizerBuilder.OwnsOne(c => c.TeethColor);
                customizerBuilder.OwnsOne(c => c.MouthColor);
                customizerBuilder.OwnsOne(c => c.ClawsColor);

                
            });
        });
        

        modelBuilder.Entity < Storage>(storageBuilder =>
        {
            storageBuilder.HasOne(s => s.Player)
                .WithOne(p => p.Storage)
                .HasForeignKey<Storage>(p => p.PlayerId);
        });

        modelBuilder.Entity<StorageSlot>(slotBuilder =>
        {
            slotBuilder.HasOne(s => s.Storage)
                .WithMany(s => s.Slots)
                .HasForeignKey(s => s.StorageId);
            slotBuilder.OwnsOne(s => s.Mutations);
            slotBuilder.OwnsOne(s => s.HealthData);
        });

        modelBuilder.Entity<PlayerInvite>(inviteBuilder =>
        {
            // Two FKs into Player; Restrict avoids the multiple-cascade-path error on delete.
            inviteBuilder.HasOne(r => r.SenderPlayer)
                .WithMany()
                .HasForeignKey(r => r.SenderPlayerId)
                .OnDelete(DeleteBehavior.Restrict);

            inviteBuilder.HasOne(r => r.ReceiverPlayer)
                .WithMany()
                .HasForeignKey(r => r.ReceiverPlayerId)
                .OnDelete(DeleteBehavior.Restrict);

            inviteBuilder.HasIndex(r => new { r.ReceiverPlayerId, r.Status });
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
    
    private static Vector3 ParseVector3(string s)
{
    var parts = s.Split(',');
    return new Vector3(float.Parse(parts[0]), float.Parse(parts[1]), float.Parse(parts[2]));
}
    
}