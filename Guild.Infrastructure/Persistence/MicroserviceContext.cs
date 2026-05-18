using System.Threading.Channels;
using AppEnvironment;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Persistence;

namespace Guild.Persistence.Persistence;

public class MicroserviceContext : Microsoft.EntityFrameworkCore.DbContext
{
    public DbSet<Domain.Aggregates.Guild> Guilds { get; set; }
    public DbSet<Domain.Aggregates.Channel> Channels { get; set; }
    public DbSet<Domain.Aggregates.Role> Roles { get; set; }
    public DbSet<Domain.Entity.PublicKeyStore> PublicKeys { get; set; }
    public DbSet<Domain.Entity.Category> Categories { get; set; }
    public DbSet<Domain.Entity.ChannelPermission> ChannelPermissions { get; set; }
    public DbSet<Domain.Entity.GuildMember> GuildMembers { get; set; }
    public DbSet<Domain.Entity.RoleMember> RoleMembers { get; set; }
    public DbSet<GuildInvite> GuildInvites { get; set; }
    public DbSet<ReadState> ReadStates { get; set; }
    public DbSet<Wiki> Wikis { get; set; }
    public DbSet<WikiPage> WikiPages { get; set; }
    public DbSet<WikiCategory> WikiCategories { get; set; }
    public DbSet<WikiRevision> WikiRevisions { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var env = Env.Database;

        optionsBuilder.UseNpgsql(env.ConnectionString(), options =>
        {
            options.MapEnum<ChannelType>();
            options.MapEnum<PermissionState>();
            options.MapEnum<EncryptionState>();
            options.MapEnum<MemberType>();
            options.MapEnum<InviteType>();
            options.MapEnum<InviteState>();
            options.MapEnum<RoleType>();
            options.MapEnum<WikiVisibility>();
        }).UseSnakeCaseNamingConvention();
    }
    public MicroserviceContext(DbContextOptions<MicroserviceContext> options) : base(options)
    {
        
    }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RoleMember>(memberBuilder =>
        {
            memberBuilder.HasOne(x => x.Role)
                .WithMany(x => x.Members)
                .HasForeignKey(x => x.RoleId)
                .OnDelete(DeleteBehavior.Cascade);

            memberBuilder.HasOne(x => x.Member)
                .WithMany(x => x.RoleMembers)
                .HasForeignKey(x => x.MemberId)
                .OnDelete(DeleteBehavior.Cascade);
        });
       
        modelBuilder.Entity<Domain.Aggregates.Guild>(guidBuilder =>
        {
         
            guidBuilder.HasOne(g => g.SystemChannel)
                .WithOne(c => c.SystemChannelGuild)
                .HasForeignKey<Domain.Aggregates.Guild>(g => g.SystemChannelId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired(false);
        });

        modelBuilder.Entity<ReadState>(readStateBuilder =>
        {
            readStateBuilder.HasOne(x => x.GuildMember)
                .WithMany(x => x.ReadStates)
                .HasForeignKey(x => x.MemberId)
                .OnDelete(DeleteBehavior.Cascade);
            readStateBuilder.HasOne(x => x.Channel)
                .WithMany(x => x.ReadStates)
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
         
        });
        
        modelBuilder.Entity<Domain.Entity.Category>(categoryBuilder =>
        {
            categoryBuilder.HasOne(x => x.Guild)
                .WithMany(x => x.Categories)
                .HasForeignKey(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        
        modelBuilder.Entity<Domain.Aggregates.Channel>(channelBuilder =>
        {
            channelBuilder.HasOne(c => c.Category)
                .WithMany(c => c.Channels)
                .HasForeignKey(c => c.CategoryId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired(false);
            channelBuilder.HasOne(x => x.Guild).WithMany(x => x.Channels).HasForeignKey(x => x.GuildId).OnDelete(DeleteBehavior.Cascade);
            
           
            
        });
        
        modelBuilder.Entity<GuildInvite>(guildInviteBuilder =>
        {
            guildInviteBuilder.HasOne(x => x.Guild)
                .WithMany(x => x.Invites)
                .HasForeignKey(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Domain.Entity.ChannelPermission>(channelPermissionBuilder =>
        {
            channelPermissionBuilder.HasOne(x => x.Role)
                .WithMany()
                .HasForeignKey(x => x.RoleId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired(false);

            channelPermissionBuilder.HasOne(x => x.Channel)
                .WithMany(x => x.Permissions)
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired(false);

            channelPermissionBuilder.HasOne(x => x.Category)
                .WithMany(x => x.Permissions)
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired(false);

            channelPermissionBuilder.HasOne(x => x.Member)
                .WithMany(x => x.PermissionOverwrites)
                .HasForeignKey(x => x.MemberId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired(false);
            
            channelPermissionBuilder
                .HasIndex(x => x.ChannelId)
                .HasDatabaseName("IX_channel_permissions_channel_id_filtered")
                .HasFilter("channel_id IS NOT NULL"); // Hardcoded to match your PG column name

            channelPermissionBuilder
                .HasIndex(x => x.CategoryId)
                .HasDatabaseName("IX_channel_permissions_category_id_filtered")
                .HasFilter("category_id IS NOT NULL"); // Hardcoded to match your PG column name

            channelPermissionBuilder
                .HasIndex(x => new { x.RoleId, x.MemberId })
                .HasDatabaseName("IX_channel_permissions_role_member");
        });

        modelBuilder.Entity<GuildMember>(guildMemberBuilder =>
        {
            guildMemberBuilder.HasOne(m => m.Guild)
                .WithMany(g => g.Members)
                .HasForeignKey(m => m.GuildId);
            
            guildMemberBuilder.HasOne(m => m.Invite)
                .WithMany(i => i.Members)
                .HasForeignKey(m => m.InviteId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired(false);

            guildMemberBuilder.HasIndex(m => new { m.GuildId, m.UserId });
            
   

        });
        
        modelBuilder.Entity<Domain.Aggregates.Role>(roleBuilder =>
        {
            roleBuilder.HasOne(x => x.Guild).WithMany(x => x.Roles).HasForeignKey(x => x.GuildId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Domain.Entity.PublicKeyStore>(keyStoreBuilder =>
        {
            keyStoreBuilder.HasOne<Domain.Aggregates.Guild>()
                .WithMany(x => x.PublicKeys)
                .HasForeignKey(x => x.GuildId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Cascade);

            keyStoreBuilder.HasOne<Domain.Aggregates.Channel>()
                .WithMany(x => x.PublicKeys)
                .HasForeignKey(x => x.GuildId) // <-- This should be ChannelId
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Wiki>(wikiBuilder =>
        {
            wikiBuilder.HasOne<Domain.Aggregates.Guild>()
                .WithOne()
                .HasForeignKey<Wiki>(w => w.GuildId)
                .OnDelete(DeleteBehavior.Cascade);

            wikiBuilder.HasIndex(w => w.GuildId).IsUnique();
        });

        modelBuilder.Entity<WikiPage>(pageBuilder =>
        {
            pageBuilder.HasMany(p => p.Revisions)
                .WithOne()
                .HasForeignKey(r => r.PageId)
                .OnDelete(DeleteBehavior.Cascade);

            pageBuilder.Property(p => p.Tags)
                .HasColumnType("text[]");

            pageBuilder.HasOne<Domain.Aggregates.Guild>()
                .WithMany()
                .HasForeignKey(p => p.GuildId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WikiCategory>(categoryBuilder =>
        {
            categoryBuilder.HasOne<Domain.Aggregates.Guild>()
                .WithMany()
                .HasForeignKey(c => c.GuildId)
                .OnDelete(DeleteBehavior.Cascade);
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