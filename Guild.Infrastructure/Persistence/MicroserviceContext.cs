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
    public DbSet<ChannelBroadcastMention> ChannelBroadcastMentions { get; set; }
    public DbSet<InboxTaskDismissal> InboxTaskDismissals { get; set; }
    public DbSet<Wiki> Wikis { get; set; }
    public DbSet<WikiPage> WikiPages { get; set; }
    public DbSet<WikiCategory> WikiCategories { get; set; }
    public DbSet<WikiRevision> WikiRevisions { get; set; }
    public DbSet<WikiPageReaction> WikiPageReactions { get; set; }
    public DbSet<WikiPageWatcher> WikiPageWatchers { get; set; }
    public DbSet<WikiComment> WikiComments { get; set; }
    public DbSet<WikiPageLink> WikiPageLinks { get; set; }

    // ── Roleplay ─────────────────────────────────────────────────────────────
    public DbSet<Persona> Personas { get; set; }
    public DbSet<PersonaGuildProfile> PersonaGuildProfiles { get; set; }
    public DbSet<PersonaGrant> PersonaGrants { get; set; }
    public DbSet<PersonaAutoproxyState> PersonaAutoproxyStates { get; set; }
    public DbSet<SceneState> SceneStates { get; set; }
    public DbSet<SceneFolder> SceneFolders { get; set; }
    public DbSet<SceneTag> SceneTags { get; set; }
    public DbSet<SceneTagAssignment> SceneTagAssignments { get; set; }
    public DbSet<SceneJoinRequest> SceneJoinRequests { get; set; }
    public DbSet<DiceRoll> DiceRolls { get; set; }

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

    public DbSet<GuildNotificationSetting> GuildNotificationSettings { get; set; }
    public DbSet<NotificationOverride> NotificationOverrides { get; set; }
    public DbSet<GuildDirectMessagePreference> GuildDirectMessagePreferences { get; set; }

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

    // ── Household modules, second wave ───────────────────────────────────────
    public DbSet<RecurringExpense> RecurringExpenses { get; set; }
    public DbSet<RecurringExpenseShare> RecurringExpenseShares { get; set; }
    public DbSet<BillOccurrence> BillOccurrences { get; set; }
    public DbSet<ExpenseReceipt> ExpenseReceipts { get; set; }
    public DbSet<PantryBarcode> PantryBarcodes { get; set; }
    public DbSet<MemberAbsence> MemberAbsences { get; set; }
    public DbSet<MaintenanceAsset> MaintenanceAssets { get; set; }
    public DbSet<MaintenanceRecord> MaintenanceRecords { get; set; }
    public DbSet<PaymentHandleBlob> PaymentHandleBlobs { get; set; }
    public DbSet<PaymentHandleKeyWrap> PaymentHandleKeyWraps { get; set; }
    public DbSet<Recipe> Recipes { get; set; }
    public DbSet<RecipeIngredient> RecipeIngredients { get; set; }
    public DbSet<MealPlanEntry> MealPlanEntries { get; set; }
    public DbSet<MealPlanConfig> MealPlanConfigs { get; set; }

    // ── Shared product catalog ───────────────────────────────────────────────
    public DbSet<ProductCatalogEntry> ProductCatalogEntries { get; set; }
    public DbSet<ProductCatalogMiss> ProductCatalogMisses { get; set; }

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
            options.MapEnum<InviteTargetType>();
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
            options.MapEnum<MealSlot>();
            options.MapEnum<RecurrenceUnit>();
            options.MapEnum<BillStatus>();
            options.MapEnum<ExpenseCategory>();
            options.MapEnum<AssetStatus>();
            options.MapEnum<PersonaScope>();
            options.MapEnum<PersonaApprovalState>();
            options.MapEnum<AutoproxyMode>();
            options.MapEnum<SceneStatus>();
            options.MapEnum<SceneJoinPolicy>();
            options.MapEnum<SceneVisibility>();
            options.MapEnum<SceneJoinRequestStatus>();
        }).UseSnakeCaseNamingConvention();
    }
    public MicroserviceContext(DbContextOptions<MicroserviceContext> options) : base(options)
    {
        
    }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Trigram matching for the product catalog's keyword search.
        modelBuilder.HasPostgresExtension("pg_trgm");

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

            // Unique across the instance, and partial so the overwhelming majority of guilds -
            // which have no vanity URL - do not all collide on NULL.
            guidBuilder.HasIndex(g => g.VanityUrl)
                .HasDatabaseName("ix_guilds_vanity_url")
                .IsUnique()
                .HasFilter("vanity_url IS NOT NULL");

            guidBuilder.Property(g => g.PrimaryLanguage).HasMaxLength(35).HasDefaultValue("en");
            guidBuilder.Property(g => g.OtherLanguages).HasColumnType("text[]").HasDefaultValueSql("'{}'::text[]");
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

            // A second row for the same pair keeps the channel unread forever: the unread query
            // left-joins every read state a member has, and the ack only ever moves the first.
            // Leads with MemberId, so it also carries the FK index and the inbox's
            // "every read state this member has" query.
            readStateBuilder.HasIndex(x => new { x.MemberId, x.ChannelId })
                .HasDatabaseName("ix_read_states_member_id_channel_id")
                .IsUnique();
        });

        modelBuilder.Entity<ChannelBroadcastMention>(broadcastBuilder =>
        {
            broadcastBuilder.HasOne(x => x.Channel)
                .WithMany()
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);

            // Read shape is always "broadcasts in these channels since this timestamp" - the unread
            // predicate and the mentions page both slice it that way.
            broadcastBuilder.HasIndex(x => new { x.ChannelId, x.MessageCreatedAt })
                .HasDatabaseName("IX_broadcast_mentions_channel_created");

            // Retention sweep orders by age across every channel at once, so it needs its own.
            broadcastBuilder.HasIndex(x => x.MessageCreatedAt)
                .HasDatabaseName("IX_broadcast_mentions_created");

            // A retried handler must not write the ping twice; the read path counts rows.
            broadcastBuilder.HasIndex(x => new { x.MessageId, x.RoleId })
                .IsUnique()
                .HasDatabaseName("IX_broadcast_mentions_message_role");
        });

        modelBuilder.Entity<InboxTaskDismissal>(dismissalBuilder =>
        {
            // One dismissal per row per person, and the read path looks the whole set up by
            // caller, so the unique index is the one it rides too.
            dismissalBuilder.HasIndex(x => new { x.UserId, x.Kind, x.GuildId, x.TargetId })
                .IsUnique()
                .HasDatabaseName("IX_inbox_task_dismissals_user_task");

            // The retention sweep each write runs walks one caller oldest-first.
            dismissalBuilder.HasIndex(x => new { x.UserId, x.DismissedAt })
                .HasDatabaseName("IX_inbox_task_dismissals_user_dismissed");
        });

        modelBuilder.Entity<GuildNotificationSetting>(settingBuilder =>
        {
            settingBuilder.HasOne(x => x.GuildMember)
                .WithMany()
                .HasForeignKey(x => x.MemberId)
                .OnDelete(DeleteBehavior.Cascade);

            // One row per member, enforced rather than merely assumed - the endpoints upsert, and
            // a duplicate would make "which of these is my setting" a coin flip.
            settingBuilder.HasIndex(x => x.MemberId).IsUnique();
        });

        modelBuilder.Entity<NotificationOverride>(overrideBuilder =>
        {
            overrideBuilder.HasOne(x => x.GuildMember)
                .WithMany()
                .HasForeignKey(x => x.MemberId)
                .OnDelete(DeleteBehavior.Cascade);

            overrideBuilder.HasOne(x => x.Channel)
                .WithMany()
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);

            overrideBuilder.HasOne(x => x.Category)
                .WithMany()
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.Cascade);

            // Filtered so the uniqueness only applies to rows of that kind - a member's category
            // override and channel override are different rows with one of the two ids null, and
            // an unfiltered composite index would treat every null-channel row as colliding.
            overrideBuilder.HasIndex(x => new { x.MemberId, x.ChannelId })
                .IsUnique()
                .HasFilter("channel_id IS NOT NULL");

            overrideBuilder.HasIndex(x => new { x.MemberId, x.CategoryId })
                .IsUnique()
                .HasFilter("category_id IS NOT NULL");
        });


        modelBuilder.Entity<GuildDirectMessagePreference>(preferenceBuilder =>
        {
            // No inverse navigation on Guild: GuildDto is Facet-generated and a collection on the
            // aggregate widens that materialization graph (same reasoning as ForumTag).
            preferenceBuilder.HasOne(x => x.Guild)
                .WithMany()
                .HasForeignKey(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);

            // One row per (user, guild), enforced rather than assumed - both the PUT endpoint and
            // the bus handler upsert on this pair, and a duplicate would make "may this person DM
            // me" a coin flip.
            preferenceBuilder.HasIndex(x => new { x.UserId, x.GuildId }).IsUnique();

            // The GET /users/me/guild-privacy shape: every override one caller holds, across guilds.
            preferenceBuilder.HasIndex(x => x.UserId);
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

            // Composite covering indexes for the two forum post orderings.
            channelBuilder.HasIndex(c => new { c.ParentChannelId, c.IsPinned, c.LastActivityAt })
                .HasDatabaseName("IX_channels_forum_activity");

            channelBuilder.HasIndex(c => new { c.ParentChannelId, c.IsPinned, c.CreatedAt })
                .HasDatabaseName("IX_channels_forum_created");

            // One thread per message, enforced here rather than by a read-then-create: two people
            // clicking the same button race, and the loser has to fail on the insert.
            channelBuilder.HasIndex(c => c.StarterMessageId)
                .IsUnique()
                .HasFilter("starter_message_id IS NOT NULL")
                .HasDatabaseName("IX_channels_starter_message");
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

            // The moderator list reads one guild's invites and hides the revoked ones by default;
            // without this that is a scan of every invite ever minted on the instance.
            guildInviteBuilder.HasIndex(x => new { x.GuildId, x.State });
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
            
            // SetNull, emphatically not Cascade.
            guildMemberBuilder.HasOne(m => m.Invite)
                .WithMany(i => i.Members)
                .HasForeignKey(m => m.InviteId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);

            guildMemberBuilder.HasIndex(m => new { m.GuildId, m.UserId });

            // The temporary-membership sweep asks for the due rows once a minute, and the rows it
            // wants are a vanishing fraction of the table - so the index is partial, and the whole
            // point is that a guild with no temporary members contributes nothing to it.
            guildMemberBuilder.HasIndex(m => m.TemporaryEvictionDueAt)
                .HasDatabaseName("ix_guild_members_temporary_eviction_due_at")
                .HasFilter("temporary_eviction_due_at IS NOT NULL");

        });
        
        modelBuilder.Entity<Domain.Aggregates.Role>(roleBuilder =>
        {
            roleBuilder.HasOne(x => x.Guild).WithMany(x => x.Roles).HasForeignKey(x => x.GuildId).OnDelete(DeleteBehavior.Cascade);

            // A guild has exactly one @everyone role, and the database is where that is enforced.
            roleBuilder.HasIndex(x => x.GuildId);

            roleBuilder.HasIndex(x => x.GuildId, "ix_roles_guild_id_everyone")
                .HasDatabaseName("ix_roles_guild_id_everyone")
                .IsUnique()
                .HasFilter("type = 'everyone'");
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

            // The public slug is a hostname-level identifier, so the database is where uniqueness is
            // decided - the publish endpoint's "is it taken" query races two guilds claiming the
            // same name at once. Filtered, because unpublished is the overwhelming majority.
            wikiBuilder.HasIndex(w => w.PublishedSlug)
                .IsUnique()
                .HasFilter("published_slug IS NOT NULL");
        });

        modelBuilder.Entity<WikiPage>(pageBuilder =>
        {
            pageBuilder.HasMany(p => p.Revisions)
                .WithOne()
                .HasForeignKey(r => r.PageId)
                .OnDelete(DeleteBehavior.Cascade);

            pageBuilder.Property(p => p.Tags)
                .HasColumnType("text[]");

            // Real jsonb, not text: this column only ever lives in Postgres, so unlike
            // Message.EmbedsJson it does not have to be storable anywhere else.
            pageBuilder.Property(p => p.InfoboxJson)
                .HasColumnType("jsonb");

            pageBuilder.HasOne<Domain.Aggregates.Guild>()
                .WithMany()
                .HasForeignKey(p => p.GuildId)
                .OnDelete(DeleteBehavior.Cascade);

            // Unlinks the page rather than deleting it - the prose outlives the persona row.
            pageBuilder.HasOne<Persona>()
                .WithMany()
                .HasForeignKey(p => p.PersonaId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);

            // One character, one page per guild. Filtered, because every ordinary page has a null
            // persona and an unfiltered index would treat them all as colliding.
            pageBuilder.HasIndex(p => new { p.GuildId, p.PersonaId })
                .IsUnique()
                .HasFilter("persona_id IS NOT NULL");
        });

        modelBuilder.Entity<WikiRevision>(revisionBuilder =>
        {
            revisionBuilder.Property(r => r.InfoboxJson)
                .HasColumnType("jsonb");
        });

        modelBuilder.Entity<WikiCategory>(categoryBuilder =>
        {
            categoryBuilder.HasOne<Domain.Aggregates.Guild>()
                .WithMany()
                .HasForeignKey(c => c.GuildId)
                .OnDelete(DeleteBehavior.Cascade);

            categoryBuilder.Property(c => c.InfoboxTemplateJson)
                .HasColumnType("jsonb");
        });

        // Reactions hang off WikiPage by foreign key with no navigation property on the page. That
        // is deliberate: WikiPageDto/WikiPageSummaryDto are Facets over WikiPage, so every
        // collection added to the entity has to be named in an exclusion list to stay out of the
        // wire shape, and any stray Include() would drag the whole set into memory the way
        // Include(p => p.Revisions) used to. They are read as an aggregate instead.
        modelBuilder.Entity<WikiPageReaction>(reactionBuilder =>
        {
            // One row per (page, user, emoji) makes reacting twice a no-op instead of a duplicate.
            reactionBuilder.HasKey(r => new { r.PageId, r.UserId, r.Emoji });

            reactionBuilder.HasOne<WikiPage>()
                .WithMany()
                .HasForeignKey(r => r.PageId)
                .OnDelete(DeleteBehavior.Cascade);

            // No separate index on PageId: it leads the primary key, whose index already serves
            // "every reaction on this page" - the only query this table has.
        });

        // Same FK-without-navigation shape as the reactions above, for the same reason.
        modelBuilder.Entity<WikiPageWatcher>(watcherBuilder =>
        {
            watcherBuilder.HasKey(w => new { w.PageId, w.UserId });

            watcherBuilder.HasOne<WikiPage>()
                .WithMany()
                .HasForeignKey(w => w.PageId)
                .OnDelete(DeleteBehavior.Cascade);

            // The fan-out on every page edit is "who watches this page", which the primary key's
            // leading column already covers.
            watcherBuilder.HasIndex(w => new { w.GuildId, w.UserId });
        });

        modelBuilder.Entity<WikiComment>(commentBuilder =>
        {
            commentBuilder.HasOne<WikiPage>()
                .WithMany()
                .HasForeignKey(c => c.PageId)
                .OnDelete(DeleteBehavior.Cascade);

            commentBuilder.HasIndex(c => new { c.PageId, c.CreatedAt });
        });

        // Same FK-without-navigation shape as the reactions above, for the same reason.
        modelBuilder.Entity<WikiPageLink>(linkBuilder =>
        {
            linkBuilder.HasKey(l => new { l.SourcePageId, l.TargetPageId });

            linkBuilder.HasOne<WikiPage>()
                .WithMany()
                .HasForeignKey(l => l.SourcePageId)
                .OnDelete(DeleteBehavior.Cascade);

            // No foreign key on TargetPageId: a link to a page that does not exist yet is a red
            // link, and an FK would refuse the write.

            // The backlinks query, which asks who points at one page across the whole guild.
            linkBuilder.HasIndex(l => new { l.GuildId, l.TargetPageId });
        });

        // Personas hang off Guild by foreign key and never off GuildMember - see
        // docs/specs/roleplay-guilds.md §2. A second row per (user, guild) in the member table
        // would make permission resolution return an arbitrary one, silently.
        modelBuilder.Entity<Persona>(personaBuilder =>
        {
            personaBuilder.HasOne<Domain.Aggregates.Guild>()
                .WithMany()
                .HasForeignKey(x => x.OwnerGuildId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Cascade);

            // The guild's shared cast, and half of the union the proxy-prefix check walks.
            personaBuilder.HasIndex(x => new { x.OwnerGuildId, x.OwnerUserId });

            // The account-level list, and the purge on account deletion. Filtered because a
            // guild-scoped persona has no owning user at all.
            personaBuilder.HasIndex(x => x.OwnerUserId)
                .HasFilter("owner_user_id IS NOT NULL");
        });

        modelBuilder.Entity<PersonaGuildProfile>(profileBuilder =>
        {
            profileBuilder.HasOne<Persona>()
                .WithMany()
                .HasForeignKey(x => x.PersonaId)
                .OnDelete(DeleteBehavior.Cascade);

            profileBuilder.HasOne<Domain.Aggregates.Guild>()
                .WithMany()
                .HasForeignKey(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);

            // Deleting the character page leaves the adoption and its approval standing.
            profileBuilder.HasOne<WikiPage>()
                .WithMany()
                .HasForeignKey(x => x.WikiPageId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);

            // Adoption is idempotent; two rows would make "which overrides apply here" a coin flip.
            profileBuilder.HasIndex(x => new { x.PersonaId, x.GuildId }).IsUnique();

            // The approval queue.
            profileBuilder.HasIndex(x => new { x.GuildId, x.ApprovalState });

            // The send path's prefix resolution, and the collision check that shares it.
            profileBuilder.HasIndex(x => new { x.GuildId, x.ProxyPrefix })
                .HasFilter("proxy_prefix IS NOT NULL");
        });

        modelBuilder.Entity<PersonaGrant>(grantBuilder =>
        {
            grantBuilder.HasOne<Persona>()
                .WithMany()
                .HasForeignKey(x => x.PersonaId)
                .OnDelete(DeleteBehavior.Cascade);

            // Deleting the role takes the grant with it, which is what "stops being selectable
            // immediately" means.
            grantBuilder.HasOne<Domain.Aggregates.Role>()
                .WithMany()
                .HasForeignKey(x => x.RoleId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Cascade);

            // Granting twice is a no-op rather than a duplicate, on either shape. That exactly one
            // of the two ids is set is enforced by PersonaGrant.Create, the only way to build one.
            grantBuilder.HasIndex(x => new { x.PersonaId, x.RoleId })
                .IsUnique()
                .HasFilter("role_id IS NOT NULL");

            grantBuilder.HasIndex(x => new { x.PersonaId, x.UserId })
                .IsUnique()
                .HasFilter("user_id IS NOT NULL");
        });

        modelBuilder.Entity<PersonaAutoproxyState>(autoproxyBuilder =>
        {
            autoproxyBuilder.HasOne<Domain.Aggregates.Channel>()
                .WithMany()
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);

            autoproxyBuilder.HasOne<Domain.Aggregates.Guild>()
                .WithMany()
                .HasForeignKey(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);

            // A retired or deleted persona leaves the row behind resolving to nothing, rather than
            // pointing at an id that is gone.
            autoproxyBuilder.HasOne<Persona>()
                .WithMany()
                .HasForeignKey(x => x.PersonaId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);

            // The send path reads this on every message with no explicit persona on it.
            autoproxyBuilder.HasIndex(x => new { x.UserId, x.ChannelId }).IsUnique();

            // Leaving a guild clears the whole set in one delete.
            autoproxyBuilder.HasIndex(x => new { x.UserId, x.GuildId });
        });

        // A side table rather than six more columns on Channel: Channel.cs earns the forum columns
        // their place by each being a sort key or a filter on the forum listing, and none of these
        // is - two of them are arrays.
        modelBuilder.Entity<SceneState>(sceneBuilder =>
        {
            sceneBuilder.HasKey(x => x.ChannelId);

            sceneBuilder.HasOne<Domain.Aggregates.Channel>()
                .WithOne()
                .HasForeignKey<SceneState>(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);

            sceneBuilder.HasOne<Domain.Aggregates.Guild>()
                .WithMany()
                .HasForeignKey(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);

            // Deleting the companion thread leaves the scene standing with nothing linked, rather
            // than a pointer at a channel that is gone.
            sceneBuilder.HasOne<Domain.Aggregates.Channel>()
                .WithMany()
                .HasForeignKey(x => x.OocThreadId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);

            // Unfiling, never a cascade: deleting a folder must not delete a campaign.
            sceneBuilder.HasOne<SceneFolder>()
                .WithMany()
                .HasForeignKey(x => x.FolderId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);

            // The one question the stale-turn sweep asks: which scenes are being played and overdue.
            sceneBuilder.HasIndex(x => new { x.Status, x.TurnDeadlineAt });

            // What the archive asks: one folder's scenes.
            sceneBuilder.HasIndex(x => new { x.GuildId, x.FolderId });
        });

        modelBuilder.Entity<SceneJoinRequest>(requestBuilder =>
        {
            requestBuilder.HasOne<Domain.Aggregates.Channel>()
                .WithMany()
                .HasForeignKey(x => x.SceneChannelId)
                .OnDelete(DeleteBehavior.Cascade);

            requestBuilder.HasOne<Domain.Aggregates.Guild>()
                .WithMany()
                .HasForeignKey(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);

            // The GM's guild-wide queue, and one scene's banner.
            requestBuilder.HasIndex(x => new { x.GuildId, x.Status });
            requestBuilder.HasIndex(x => new { x.SceneChannelId, x.Status });

            // Partial, so a character that was denied can ask again while never queueing twice.
            requestBuilder.HasIndex(x => new { x.SceneChannelId, x.PersonaId })
                .IsUnique()
                .HasFilter("status = 'pending'");
        });

        modelBuilder.Entity<SceneFolder>(folderBuilder =>
        {
            folderBuilder.HasOne<Domain.Aggregates.Guild>()
                .WithMany()
                .HasForeignKey(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);

            // Deleting a parent leaves its children standing at the root rather than taking a
            // guild's whole arc structure with it.
            folderBuilder.HasOne<SceneFolder>()
                .WithMany()
                .HasForeignKey(x => x.ParentFolderId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);

            folderBuilder.HasIndex(x => new { x.GuildId, x.Position });
        });

        modelBuilder.Entity<SceneTag>(sceneTagBuilder =>
        {
            sceneTagBuilder.HasOne<Domain.Aggregates.Guild>()
                .WithMany()
                .HasForeignKey(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);

            sceneTagBuilder.HasIndex(x => new { x.GuildId, x.Position });

            // Backstop against exact-duplicate names only.
            sceneTagBuilder.HasIndex(x => new { x.GuildId, x.Name }).IsUnique();
        });

        modelBuilder.Entity<SceneTagAssignment>(assignmentBuilder =>
        {
            assignmentBuilder.HasKey(x => new { x.SceneChannelId, x.TagId });

            assignmentBuilder.HasOne<Domain.Aggregates.Channel>()
                .WithMany()
                .HasForeignKey(x => x.SceneChannelId)
                .OnDelete(DeleteBehavior.Cascade);

            assignmentBuilder.HasOne<SceneTag>()
                .WithMany()
                .HasForeignKey(x => x.TagId)
                .OnDelete(DeleteBehavior.Cascade);

            // The PK covers "tags of this scene"; this covers the inverse, "scenes carrying this
            // tag", which is what the archive filter runs.
            assignmentBuilder.HasIndex(x => x.TagId);
        });

        modelBuilder.Entity<DiceRoll>(diceBuilder =>
        {
            diceBuilder.HasOne<Domain.Aggregates.Guild>()
                .WithMany()
                .HasForeignKey(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);

            diceBuilder.HasOne<Domain.Aggregates.Channel>()
                .WithMany()
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);

            // A retired character keeps its rolls; a deleted one leaves them attributed to the
            // account that rolled, which is the field moderation reads anyway.
            diceBuilder.HasOne<Persona>()
                .WithMany()
                .HasForeignKey(x => x.PersonaId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);

            // An integer rather than a Postgres enum: adding a HasPostgresEnum member later needs a
            // migration in front of it or the service crashes at startup, and per-recipient
            // visibility is exactly the change that will add members here.
            diceBuilder.Property(x => x.Visibility).HasConversion<int>();

            // The message is the key: one message is one roll.
            diceBuilder.HasIndex(x => x.MessageId).IsUnique();

            diceBuilder.HasIndex(x => new { x.ChannelId, x.CreatedAt });
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
            // No FK to Guild - SourceGuildId is advisory-only and must survive the source guild
            // being deleted (see TemplateSnapshot's doc comment).
            templateBuilder.OwnsOne(x => x.Snapshot, snapshotBuilder =>
            {
                snapshotBuilder.ToJson();
                snapshotBuilder.OwnsMany(s => s.Roles);
                snapshotBuilder.OwnsMany(s => s.UncategorizedChannels, channelBuilder =>
                {
                    channelBuilder.OwnsMany(c => c.Overwrites);
                });
                snapshotBuilder.OwnsMany(s => s.Categories, categoryBuilder =>
                {
                    categoryBuilder.OwnsMany(c => c.Overwrites);
                    categoryBuilder.OwnsMany(c => c.Channels, channelBuilder =>
                    {
                        channelBuilder.OwnsMany(c => c.Overwrites);
                    });
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
            // HasOne<Channel>() with no inverse navigation: the Channel aggregate deliberately owns
            // no ForumTag collection (see ForumTag's doc comment - ChannelDto is Facet-generated
            // and nested inside GuildDto, so collections there widen the materialization graph).
            tagBuilder.HasOne<Domain.Aggregates.Channel>()
                .WithMany()
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);

            tagBuilder.HasIndex(x => new { x.ChannelId, x.Position });

            // Backstop against exact-duplicate names only.
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

            // The expiry sweep's query: dated and not yet warned about.
            itemBuilder.HasIndex(x => x.ExpiresAt)
                .HasFilter("expiry_notified_at IS NULL AND expires_at IS NOT NULL");

            // Scan's first question: is the thing I am holding already in this fridge.
            itemBuilder.HasIndex(x => new { x.ChannelId, x.Barcode })
                .HasFilter("barcode IS NOT NULL");
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

            // The reminder sweep's query: unreminded, unfinished, and due.
            occurrenceBuilder.HasIndex(x => x.DueAt)
                .HasFilter("reminded_at IS NULL AND completed_at IS NULL AND skipped_at IS NULL");
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

        // ── Household modules, second wave ───────────────────────────────────
        // Same shadow-side-FK-to-Channel rule as above; see the note on the first wave.

        modelBuilder.Entity<RecurringExpense>(templateBuilder =>
        {
            templateBuilder.HasOne<Domain.Aggregates.Channel>()
                .WithMany()
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);

            templateBuilder.HasIndex(x => x.ChannelId);

            // The generation sweep's query: unpaused templates whose next slot is inside the
            // widest lead window. Filtered, because a paused template is not a candidate.
            templateBuilder.HasIndex(x => x.NextDueAt).HasFilter("is_paused = false");
        });

        modelBuilder.Entity<RecurringExpenseShare>(shareBuilder =>
        {
            shareBuilder.HasKey(x => new { x.RecurringExpenseId, x.UserId });

            shareBuilder.HasOne(x => x.RecurringExpense)
                .WithMany(x => x.Shares)
                .HasForeignKey(x => x.RecurringExpenseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BillOccurrence>(occurrenceBuilder =>
        {
            occurrenceBuilder.HasOne<RecurringExpense>()
                .WithMany()
                .HasForeignKey(x => x.RecurringExpenseId)
                .OnDelete(DeleteBehavior.Cascade);

            occurrenceBuilder.HasIndex(x => new { x.ChannelId, x.DueAt });

            // One occurrence per template per due date - the guard that makes generation
            // idempotent when the sweep and the create endpoint fire for the same slot.
            occurrenceBuilder.HasIndex(x => new { x.RecurringExpenseId, x.DueAt }).IsUnique();

            // The alert sweep's query.
            occurrenceBuilder.HasIndex(x => x.DueAt).HasFilter("reminded_at IS NULL");
        });

        modelBuilder.Entity<ExpenseReceipt>(receiptBuilder =>
        {
            receiptBuilder.HasOne(x => x.Expense)
                .WithMany(x => x.Receipts)
                .HasForeignKey(x => x.ExpenseId)
                .OnDelete(DeleteBehavior.Cascade);

            receiptBuilder.HasIndex(x => x.ExpenseId);
        });

        modelBuilder.Entity<PantryBarcode>(barcodeBuilder =>
        {
            barcodeBuilder.HasOne<Domain.Aggregates.Guild>()
                .WithMany()
                .HasForeignKey(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);

            // The upsert every scan runs, and the constraint that makes "learned once" true.
            barcodeBuilder.HasIndex(x => new { x.GuildId, x.Barcode }).IsUnique();

            // The completion list: this guild's products, most-used first.
            barcodeBuilder.HasIndex(x => new { x.GuildId, x.TimesSeen });
        });

        // The shared product catalog, and the two things that make it different from every other
        // table here: it belongs to no guild, and it is a Derivative Database under ODbL 1.0 that
        // we are obliged to publish.
        modelBuilder.Entity<ProductCatalogEntry>(catalogBuilder =>
        {
            // The barcode is the key.
            catalogBuilder.HasKey(x => x.Barcode);

            catalogBuilder.Property(x => x.Barcode).HasMaxLength(ProductCatalogEntry.MaxBarcodeLength);
            catalogBuilder.Property(x => x.NameDe).HasMaxLength(ProductCatalogEntry.MaxNameLength);
            catalogBuilder.Property(x => x.NameFr).HasMaxLength(ProductCatalogEntry.MaxNameLength);
            catalogBuilder.Property(x => x.NameIt).HasMaxLength(ProductCatalogEntry.MaxNameLength);
            catalogBuilder.Property(x => x.NameEn).HasMaxLength(ProductCatalogEntry.MaxNameLength);
            catalogBuilder.Property(x => x.Brand).HasMaxLength(ProductCatalogEntry.MaxBrandLength);
            catalogBuilder.Property(x => x.QuantityUnit).HasMaxLength(ProductCatalogEntry.MaxUnitLength);
            catalogBuilder.Property(x => x.Source).HasMaxLength(ProductCatalogEntry.MaxSourceLength);
            catalogBuilder.Property(x => x.SourceVersion)
                .HasMaxLength(ProductCatalogEntry.MaxSourceVersionLength);

            // Pack size, so grams and millilitres to three places is more than any packaging needs.
            catalogBuilder.Property(x => x.Quantity).HasPrecision(12, 3);

            // What a stale-row sweep and the /pantry/catalog summary both order by.
            catalogBuilder.HasIndex(x => x.ImportedAt);

            // Keyword search.
            catalogBuilder.Property(x => x.SearchText)
                .HasComputedColumnSql(
                    """
                    coalesce(name_de, '') || ' ' || coalesce(name_fr, '') || ' ' ||
                    coalesce(name_it, '') || ' ' || coalesce(name_en, '') || ' ' ||
                    coalesce(brand, '')
                    """,
                    stored: true);

            // Trigram rather than tsvector, deliberately.
            catalogBuilder.HasIndex(x => x.SearchText)
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops");
        });

        modelBuilder.Entity<ProductCatalogMiss>(missBuilder =>
        {
            missBuilder.HasKey(x => x.Barcode);

            missBuilder.Property(x => x.Barcode).HasMaxLength(ProductCatalogEntry.MaxBarcodeLength);
            missBuilder.Property(x => x.Source).HasMaxLength(ProductCatalogEntry.MaxSourceLength);

            // The live filler's work queue: misses that are due, oldest due first.
            missBuilder.HasIndex(x => x.RetryAfter).HasFilter("retry_after IS NOT NULL");
        });

        // Opaque by construction: the server stores these bytes and holds no key that opens them.
        modelBuilder.Entity<PaymentHandleBlob>(blobBuilder =>
        {
            blobBuilder.HasOne<Domain.Aggregates.Guild>()
                .WithMany()
                .HasForeignKey(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);

            // One sealed payload per person per guild; the write path upserts on this.
            blobBuilder.HasIndex(x => new { x.GuildId, x.UserId }).IsUnique();
        });

        modelBuilder.Entity<PaymentHandleKeyWrap>(wrapBuilder =>
        {
            wrapBuilder.HasKey(x => new { x.PaymentHandleBlobId, x.RecipientDeviceId });

            wrapBuilder.HasOne(x => x.Blob)
                .WithMany(x => x.Wraps)
                .HasForeignKey(x => x.PaymentHandleBlobId)
                .OnDelete(DeleteBehavior.Cascade);

            // The read path is "every blob in this guild, wraps for this one device".
            wrapBuilder.HasIndex(x => x.RecipientDeviceId);
        });

        modelBuilder.Entity<MaintenanceAsset>(assetBuilder =>
        {
            assetBuilder.HasOne<Domain.Aggregates.Channel>()
                .WithMany()
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);

            assetBuilder.HasIndex(x => new { x.ChannelId, x.Name });

            // Drives the guild-wide attention board across every maintenance channel.
            assetBuilder.HasIndex(x => new { x.GuildId, x.Status });

            // The service sweep's query: scheduled and not yet announced.
            assetBuilder.HasIndex(x => x.NextServiceAt)
                .HasFilter("service_notified_at IS NULL AND next_service_at IS NOT NULL");

            // The warranty sweep's query, filtered for the same reason.
            assetBuilder.HasIndex(x => x.WarrantyUntil)
                .HasFilter("warranty_notified_at IS NULL AND warranty_until IS NOT NULL");
        });

        modelBuilder.Entity<MaintenanceRecord>(recordBuilder =>
        {
            recordBuilder.HasOne<Domain.Aggregates.Channel>()
                .WithMany()
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);

            // Deleting a catalogued asset must not delete the history of what was done to it - the
            // receipt for last year's boiler service is the reason the log exists.
            recordBuilder.HasOne<MaintenanceAsset>()
                .WithMany()
                .HasForeignKey(x => x.AssetId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);

            // The channel log, newest first - the keyset page order.
            recordBuilder.HasIndex(x => new { x.ChannelId, x.PerformedAt });

            // The per-asset history shown on an asset's own page.
            recordBuilder.HasIndex(x => new { x.AssetId, x.PerformedAt });
        });

        modelBuilder.Entity<MemberAbsence>(absenceBuilder =>
        {
            absenceBuilder.HasOne<Domain.Aggregates.Guild>()
                .WithMany()
                .HasForeignKey(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);

            // The rotation's question: who in this guild is away at a given instant.
            absenceBuilder.HasIndex(x => new { x.GuildId, x.StartAt, x.EndAt });

            // The board's question, and the per-member overlap and count guards on write.
            absenceBuilder.HasIndex(x => new { x.GuildId, x.UserId, x.EndAt });
        });

        modelBuilder.Entity<Recipe>(recipeBuilder =>
        {
            recipeBuilder.HasOne<Domain.Aggregates.Channel>()
                .WithMany()
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);

            // The cookbook board: this channel's recipes in title order, which is also the
            // paging key.
            recipeBuilder.HasIndex(x => new { x.ChannelId, x.Title });
        });

        modelBuilder.Entity<RecipeIngredient>(ingredientBuilder =>
        {
            ingredientBuilder.HasKey(x => new { x.RecipeId, x.Position });

            ingredientBuilder.HasOne(x => x.Recipe)
                .WithMany(x => x.Ingredients)
                .HasForeignKey(x => x.RecipeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MealPlanEntry>(entryBuilder =>
        {
            entryBuilder.HasOne<Domain.Aggregates.Channel>()
                .WithMany()
                .HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);

            // Deleting a recipe must not delete the week's plan - the entry keeps its free text
            // or simply loses its link, which is what a cook expects after tidying the cookbook.
            entryBuilder.HasOne<Recipe>()
                .WithMany()
                .HasForeignKey(x => x.RecipeId)
                .OnDelete(DeleteBehavior.SetNull);

            // The board query: this channel's plan across a date window, in reading order.
            entryBuilder.HasIndex(x => new { x.ChannelId, x.Date, x.Slot, x.Position });

            // The cooking-today sweep's query: unnotified entries that have a cook and are due.
            entryBuilder.HasIndex(x => x.Date)
                .HasFilter("notified_at IS NULL AND cook_user_id IS NOT NULL");
        });

        modelBuilder.Entity<MealPlanConfig>(configBuilder =>
        {
            configBuilder.HasKey(x => x.ChannelId);

            configBuilder.HasOne<Domain.Aggregates.Channel>()
                .WithOne()
                .HasForeignKey<MealPlanConfig>(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RoleMember>(roleMemberBuilder =>
        {
            // Guest-role expiry is filtered on every permission resolution, so it needs to be
            // cheap - see GuildPermissionService.GetMembershipAsync.
            roleMemberBuilder.HasIndex(x => x.ExpiresAt);

            // A member either holds a role or does not; there is no meaning to holding it twice.
            roleMemberBuilder.HasIndex(x => new { x.RoleId, x.MemberId }).IsUnique();
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