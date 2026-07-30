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
    public DbSet<GuildOnboardingPrompt> GuildOnboardingPrompts { get; set; }
    public DbSet<GuildOnboardingPromptOption> GuildOnboardingPromptOptions { get; set; }
    public DbSet<GuildMemberOnboardingResponse> GuildMemberOnboardingResponses { get; set; }
    public DbSet<GuildOnboardingGrant> GuildOnboardingGrants { get; set; }
    public DbSet<GuildWelcomeScreen> GuildWelcomeScreens { get; set; }
    public DbSet<GuildWelcomeChannel> GuildWelcomeChannels { get; set; }
    public DbSet<GuildScheduledEvent> GuildScheduledEvents { get; set; }
    public DbSet<GuildScheduledEventInterest> GuildScheduledEventInterests { get; set; }
    public DbSet<GuildTemplate> GuildTemplates { get; set; }
    public DbSet<GuildChannelFollow> GuildChannelFollows { get; set; }
    public DbSet<ForumTag> ForumTags { get; set; }
    public DbSet<ForumPostTag> ForumPostTags { get; set; }
    public DbSet<ForumConfig> ForumConfigs { get; set; }

    // ── Household modules ────────────────────────────────────────────────────
    public DbSet<ListItem> ListItems { get; set; }
    public DbSet<PantryItem> PantryItems { get; set; }
    public DbSet<PantryConfig> PantryConfigs { get; set; }
    public DbSet<Chore> Chores { get; set; }
    public DbSet<ChoreOccurrence> ChoreOccurrences { get; set; }
    public DbSet<Expense> Expenses { get; set; }
    public DbSet<ExpenseShare> ExpenseShares { get; set; }
    public DbSet<Settlement> Settlements { get; set; }
    public DbSet<LedgerConfig> LedgerConfigs { get; set; }
    public DbSet<Decision> Decisions { get; set; }
    public DbSet<DecisionOption> DecisionOptions { get; set; }
    public DbSet<DecisionVote> DecisionVotes { get; set; }
    public DbSet<GuildQuietHoursConfig> GuildQuietHoursConfigs { get; set; }

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
            // GuildKind only - GuildFeatures is a [Flags] ulong and maps to numeric(20,0) the
            // same way Permissions already does, with no enum type in Postgres.
            options.MapEnum<GuildKind>();
            options.MapEnum<GuildScheduledEventStatus>();
            options.MapEnum<OnboardingPromptType>();
            options.MapEnum<OnboardingMode>();
            options.MapEnum<ForumSortOrder>();
            options.MapEnum<ForumLayout>();
            options.MapEnum<ExpenseSplitKind>();
            options.MapEnum<DecisionStatus>();
            options.MapEnum<DecisionVoteKind>();
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

            // Composite covering indexes for the two forum post orderings. The pre-existing
            // single-column ParentChannelId index can't serve a keyset page - it finds the
            // forum's posts but leaves the sort and the cursor comparison to a sort node.
            channelBuilder.HasIndex(c => new { c.ParentChannelId, c.IsPinned, c.LastActivityAt })
                .HasDatabaseName("IX_channels_forum_activity");

            channelBuilder.HasIndex(c => new { c.ParentChannelId, c.IsPinned, c.CreatedAt })
                .HasDatabaseName("IX_channels_forum_created");
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

        modelBuilder.Entity<GuildOnboardingPrompt>(promptBuilder =>
        {
            promptBuilder.HasOne(x => x.Guild)
                .WithMany()
                .HasForeignKey(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);

            promptBuilder.HasMany(x => x.Options)
                .WithOne(x => x.Prompt)
                .HasForeignKey(x => x.PromptId)
                .OnDelete(DeleteBehavior.Cascade);

            promptBuilder.HasIndex(x => new { x.GuildId, x.Position });
        });

        modelBuilder.Entity<GuildOnboardingPromptOption>(optionBuilder =>
        {
            optionBuilder.Property(x => x.RoleIds).HasColumnType("text[]");
            optionBuilder.Property(x => x.ChannelIds).HasColumnType("text[]");

            optionBuilder.HasIndex(x => new { x.PromptId, x.Position });
        });

        modelBuilder.Entity<GuildMemberOnboardingResponse>(responseBuilder =>
        {
            responseBuilder.HasKey(x => new { x.MemberId, x.OptionId });

            responseBuilder.HasOne(x => x.Member)
                .WithMany()
                .HasForeignKey(x => x.MemberId)
                .OnDelete(DeleteBehavior.Cascade);

            // Editing an option away deletes the answers to it; the grants it produced survive
            // (see GuildOnboardingGrant).
            responseBuilder.HasOne(x => x.Option)
                .WithMany()
                .HasForeignKey(x => x.OptionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GuildOnboardingGrant>(grantBuilder =>
        {
            grantBuilder.HasOne(x => x.Member)
                .WithMany()
                .HasForeignKey(x => x.MemberId)
                .OnDelete(DeleteBehavior.Cascade);

            // No FK on OptionId on purpose - a grant outlives the option that caused it.
            grantBuilder.HasIndex(x => new { x.MemberId, x.OptionId });
        });

        modelBuilder.Entity<GuildWelcomeScreen>(welcomeBuilder =>
        {
            welcomeBuilder.HasKey(x => x.GuildId);

            welcomeBuilder.HasOne(x => x.Guild)
                .WithOne()
                .HasForeignKey<GuildWelcomeScreen>(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);

            welcomeBuilder.HasMany(x => x.Channels)
                .WithOne(x => x.WelcomeScreen)
                .HasForeignKey(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GuildWelcomeChannel>(welcomeChannelBuilder =>
        {
            welcomeChannelBuilder.HasIndex(x => new { x.GuildId, x.Position });
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
                snapshotBuilder.OwnsOne(s => s.Onboarding, onboardingBuilder =>
                {
                    onboardingBuilder.OwnsMany(o => o.Prompts, promptBuilder =>
                    {
                        promptBuilder.OwnsMany(p => p.Options);
                    });
                });
            });
        });

        modelBuilder.Entity<GuildChannelFollow>(followBuilder =>
        {
            followBuilder.HasIndex(x => x.SourceChannelId);
            followBuilder.HasIndex(x => new { x.SourceChannelId, x.TargetChannelId }).IsUnique();
        });

        modelBuilder.Entity<ForumTag>(tagBuilder =>
        {
            // HasOne<Channel>() with no inverse navigation: the Channel aggregate deliberately
            // owns no ForumTag collection (see ForumTag's doc comment - ChannelDto is
            // Facet-generated and nested inside GuildDto, so collections there widen the
            // materialization graph). Cascade still applies; deleting a forum drops its tags.
            tagBuilder.HasOne<Domain.Aggregates.Channel>()
                .WithMany()
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);

            tagBuilder.HasIndex(x => new { x.ChannelId, x.Position });

            // Backstop against exact-duplicate names only. Case-insensitive uniqueness ("Bug" vs
            // "bug") is enforced in the endpoint with a ToLower() probe, the same split
            // GuildEmoji uses - it buys a 409 with a usable message instead of a DB violation.
            tagBuilder.HasIndex(x => new { x.ChannelId, x.Name }).IsUnique();
        });

        modelBuilder.Entity<ForumPostTag>(postTagBuilder =>
        {
            postTagBuilder.HasKey(x => new { x.ThreadChannelId, x.TagId });

            postTagBuilder.HasOne<Domain.Aggregates.Channel>()
                .WithMany()
                .HasForeignKey(x => x.ThreadChannelId)
                .OnDelete(DeleteBehavior.Cascade);

            postTagBuilder.HasOne<ForumTag>()
                .WithMany()
                .HasForeignKey(x => x.TagId)
                .OnDelete(DeleteBehavior.Cascade);

            // The PK covers "tags of this post"; this covers the inverse, "posts carrying this
            // tag", which is what the forum filter actually runs.
            postTagBuilder.HasIndex(x => x.TagId);
        });

        modelBuilder.Entity<ForumConfig>(configBuilder =>
        {
            configBuilder.HasKey(x => x.ChannelId);

            configBuilder.HasOne<Domain.Aggregates.Channel>()
                .WithOne()
                .HasForeignKey<ForumConfig>(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Household modules ────────────────────────────────────────────────
        // Every one of these hangs off Channel with HasOne<Channel>() and NO inverse navigation,
        // for the reason spelled out on ForumTag: ChannelDto is Facet-generated and nested inside
        // GuildDto, so a collection on the Channel aggregate widens that materialization graph and
        // has crashed it before. Cascade delete still applies - deleting a list drops its items.

        modelBuilder.Entity<ListItem>(itemBuilder =>
        {
            itemBuilder.HasOne<Domain.Aggregates.Channel>()
                .WithMany()
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);

            // The board query is "unchecked items of this list, in order".
            itemBuilder.HasIndex(x => new { x.ChannelId, x.IsChecked, x.Position });
        });

        modelBuilder.Entity<PantryItem>(itemBuilder =>
        {
            itemBuilder.HasOne<Domain.Aggregates.Channel>()
                .WithMany()
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);

            itemBuilder.HasIndex(x => new { x.ChannelId, x.Name });

            // Drives the "eat me first" board across every pantry in a guild.
            itemBuilder.HasIndex(x => new { x.GuildId, x.ExpiresAt });
        });

        modelBuilder.Entity<PantryConfig>(configBuilder =>
        {
            configBuilder.HasKey(x => x.ChannelId);

            configBuilder.HasOne<Domain.Aggregates.Channel>()
                .WithOne()
                .HasForeignKey<PantryConfig>(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Chore>(choreBuilder =>
        {
            choreBuilder.HasOne<Domain.Aggregates.Channel>()
                .WithMany()
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);

            // The reconcile sweep's query: unpaused chores whose next occurrence is already due.
            choreBuilder.HasIndex(x => new { x.IsPaused, x.NextDueAt });
            choreBuilder.HasIndex(x => x.ChannelId);
        });

        modelBuilder.Entity<ChoreOccurrence>(occurrenceBuilder =>
        {
            occurrenceBuilder.HasOne<Chore>()
                .WithMany()
                .HasForeignKey(x => x.ChoreId)
                .OnDelete(DeleteBehavior.Cascade);

            occurrenceBuilder.HasIndex(x => new { x.ChannelId, x.DueAt });

            // The fairness balance: completed effort per member over a window.
            occurrenceBuilder.HasIndex(x => new { x.GuildId, x.AssignedUserId, x.CompletedAt });

            // One occurrence per chore per due date - the guard that makes generation idempotent
            // when both ScheduleAsync and the reconcile sweep fire for the same slot.
            occurrenceBuilder.HasIndex(x => new { x.ChoreId, x.DueAt }).IsUnique();
        });

        modelBuilder.Entity<Expense>(expenseBuilder =>
        {
            expenseBuilder.HasOne<Domain.Aggregates.Channel>()
                .WithMany()
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);

            expenseBuilder.HasIndex(x => new { x.ChannelId, x.OccurredAt });
        });

        modelBuilder.Entity<ExpenseShare>(shareBuilder =>
        {
            shareBuilder.HasKey(x => new { x.ExpenseId, x.UserId });

            shareBuilder.HasOne(x => x.Expense)
                .WithMany(x => x.Shares)
                .HasForeignKey(x => x.ExpenseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Settlement>(settlementBuilder =>
        {
            settlementBuilder.HasOne<Domain.Aggregates.Channel>()
                .WithMany()
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);

            settlementBuilder.HasIndex(x => new { x.ChannelId, x.SettledAt });
        });

        modelBuilder.Entity<LedgerConfig>(configBuilder =>
        {
            configBuilder.HasKey(x => x.ChannelId);

            configBuilder.HasOne<Domain.Aggregates.Channel>()
                .WithOne()
                .HasForeignKey<LedgerConfig>(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Decision>(decisionBuilder =>
        {
            decisionBuilder.HasOne<Domain.Aggregates.Channel>()
                .WithMany()
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);

            decisionBuilder.HasIndex(x => new { x.ChannelId, x.Status });

            // The sweep that expires decisions whose ClosesAt has passed.
            decisionBuilder.HasIndex(x => new { x.Status, x.ClosesAt });
        });

        modelBuilder.Entity<DecisionOption>(optionBuilder =>
        {
            optionBuilder.HasOne<Decision>()
                .WithMany(x => x.Options)
                .HasForeignKey(x => x.DecisionId)
                .OnDelete(DeleteBehavior.Cascade);

            optionBuilder.HasIndex(x => new { x.DecisionId, x.Position });
        });

        modelBuilder.Entity<DecisionVote>(voteBuilder =>
        {
            // One vote per member per decision; re-voting replaces rather than accumulates.
            voteBuilder.HasKey(x => new { x.DecisionId, x.UserId });

            voteBuilder.HasOne<Decision>()
                .WithMany(x => x.Votes)
                .HasForeignKey(x => x.DecisionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GuildQuietHoursConfig>(configBuilder =>
        {
            configBuilder.HasKey(x => x.GuildId);

            configBuilder.HasOne(x => x.Guild)
                .WithOne()
                .HasForeignKey<GuildQuietHoursConfig>(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RoleMember>(roleMemberBuilder =>
        {
            // Guest-role expiry is filtered on every permission resolution, so it needs to be
            // cheap - see GuildPermissionService.GetMembershipAsync.
            roleMemberBuilder.HasIndex(x => x.ExpiresAt);
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