using System.Threading.Channels;
using AppEnvironment;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Persistence;

namespace Guild.Persistence.Persistence;

public class MicroserviceContext : DbContext
{
    public DbSet<Domain.Aggregates.Guild> Guilds { get; set; }
    public DbSet<Domain.Aggregates.Channel> Channels { get; set; }
    public DbSet<Domain.Aggregates.Role> Roles { get; set; }
    public DbSet<PublicKeyStore> PublicKeys { get; set; }
    public DbSet<Category> Categories { get; set; }
    public DbSet<ChannelPermission> ChannelPermissions { get; set; }
    public DbSet<GuildMember> GuildMembers { get; set; }
    public DbSet<RoleMember> RoleMembers { get; set; }
    public DbSet<GuildInvite> GuildInvites { get; set; }
    public DbSet<ReadState> ReadStates { get; set; }
    public DbSet<Wiki> Wikis { get; set; }
    public DbSet<WikiPage> WikiPages { get; set; }
    public DbSet<WikiCategory> WikiCategories { get; set; }
    public DbSet<WikiRevision> WikiRevisions { get; set; }
    
    public DbSet<WebhookConfig> WebhookConfigs { get; set; }
    public DbSet<GuildAuditLogEntry> AuditLogEntries { get; set; }
    public DbSet<GuildBan> GuildBans { get; set; }
    public DbSet<GuildEmoji> GuildEmojis { get; set; }
    public DbSet<GuildAutoModConfig> GuildAutoModConfigs { get; set; }
    public DbSet<GuildOnboardingConfig> GuildOnboardingConfigs { get; set; }
    public DbSet<GuildScheduledEvent> GuildScheduledEvents { get; set; }
    public DbSet<GuildScheduledEventInterest> GuildScheduledEventInterests { get; set; }
    public DbSet<GuildTemplate> GuildTemplates { get; set; }
    public DbSet<GuildChannelFollow> GuildChannelFollows { get; set; }

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
            options.MapEnum<AuditActionType>();
            options.MapEnum<GuildVerificationLevel>();
            options.MapEnum<GuildScheduledEventStatus>();
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
       
        modelBuilder.Entity<WebhookConfig>(webhookConfigBuilder =>
        {
            webhookConfigBuilder.HasOne(x => x.Guild)
                .WithMany(x => x.WebhookConfigs)
                .HasForeignKey(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);
            
            webhookConfigBuilder.HasOne(x => x.Channel)
                .WithMany(x => x.WebhookConfigs)
                .HasForeignKey(x => x.ChannelId)
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
        
        modelBuilder.Entity<Category>(categoryBuilder =>
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

            channelBuilder.HasOne(c => c.ParentChannel)
                .WithMany()
                .HasForeignKey(c => c.ParentChannelId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired(false);

        });
        
        modelBuilder.Entity<GuildInvite>(guildInviteBuilder =>
        {
            guildInviteBuilder.HasOne(x => x.Guild)
                .WithMany(x => x.Invites)
                .HasForeignKey(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);

            guildInviteBuilder.HasOne(x => x.Channel)
                .WithMany()
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);

            guildInviteBuilder.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<ChannelPermission>(channelPermissionBuilder =>
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
                .HasForeignKey(m => m.GuildId)
                .OnDelete(DeleteBehavior.Cascade);
            
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

        modelBuilder.Entity<PublicKeyStore>(keyStoreBuilder =>
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

        modelBuilder.Entity<GuildAuditLogEntry>(auditLogBuilder =>
        {
            auditLogBuilder.HasOne(x => x.Guild)
                .WithMany()
                .HasForeignKey(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);

            auditLogBuilder.HasIndex(x => new { x.GuildId, x.CreatedAt });
        });

        modelBuilder.Entity<GuildBan>(banBuilder =>
        {
            banBuilder.HasOne(x => x.Guild)
                .WithMany()
                .HasForeignKey(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);

            banBuilder.HasIndex(x => new { x.GuildId, x.BannedUserId }).IsUnique();
        });

        modelBuilder.Entity<GuildEmoji>(emojiBuilder =>
        {
            emojiBuilder.HasOne(x => x.Guild)
                .WithMany()
                .HasForeignKey(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);

            emojiBuilder.HasIndex(x => new { x.GuildId, x.Name }).IsUnique();
        });

        modelBuilder.Entity<GuildAutoModConfig>(autoModBuilder =>
        {
            autoModBuilder.HasKey(x => x.GuildId);

            autoModBuilder.HasOne(x => x.Guild)
                .WithOne()
                .HasForeignKey<GuildAutoModConfig>(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);

            autoModBuilder.Property(x => x.BlockedWords).HasColumnType("text[]");
        });

        modelBuilder.Entity<GuildOnboardingConfig>(onboardingBuilder =>
        {
            onboardingBuilder.HasKey(x => x.GuildId);

            onboardingBuilder.HasOne(x => x.Guild)
                .WithOne()
                .HasForeignKey<GuildOnboardingConfig>(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);

            onboardingBuilder.Property(x => x.DefaultChannelIds).HasColumnType("text[]");
        });

        modelBuilder.Entity<GuildScheduledEvent>(eventBuilder =>
        {
            eventBuilder.HasOne(x => x.Guild)
                .WithMany()
                .HasForeignKey(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);

            eventBuilder.HasOne(x => x.VoiceChannel)
                .WithMany()
                .HasForeignKey(x => x.VoiceChannelId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);

            eventBuilder.HasIndex(x => new { x.GuildId, x.StartsAt });
        });

        modelBuilder.Entity<GuildScheduledEventInterest>(interestBuilder =>
        {
            interestBuilder.HasKey(x => new { x.EventId, x.UserId });

            interestBuilder.HasOne(x => x.Event)
                .WithMany(x => x.Interested)
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GuildTemplate>(templateBuilder =>
        {
            // No FK to Guild - SourceGuildId is advisory-only and must survive the source
            // guild being deleted (see TemplateSnapshot's doc comment). Every nested
            // collection needs its own explicit OwnsMany - ToJson() on the root doesn't
            // recurse into further-nested owned collections automatically.
            templateBuilder.OwnsOne(x => x.Snapshot, snapshotBuilder =>
            {
                snapshotBuilder.ToJson();
                snapshotBuilder.OwnsMany(s => s.Roles);
                snapshotBuilder.OwnsMany(s => s.UncategorizedChannels);
                snapshotBuilder.OwnsMany(s => s.Categories, categoryBuilder =>
                {
                    categoryBuilder.OwnsMany(c => c.Channels);
                });
            });
        });

        modelBuilder.Entity<GuildChannelFollow>(followBuilder =>
        {
            followBuilder.HasIndex(x => x.SourceChannelId);
            followBuilder.HasIndex(x => new { x.SourceChannelId, x.TargetChannelId }).IsUnique();
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