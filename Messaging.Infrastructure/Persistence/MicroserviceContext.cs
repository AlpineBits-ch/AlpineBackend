using AppEnvironment;
using Messaging.Domain.Aggregates;
using Messaging.Domain.Entities;
using Messaging.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Persistence;

namespace Messaging.Infrastructure.Persistence;

public class MicroserviceContext : DbContext
{
    public DbSet<Conversation> Conversations { get; set; }
    public DbSet<ConversationMember> Members { get; set; }
    public DbSet<ConversationMemberDevice> MemberDevices { get; set; }
    public DbSet<PendingWelcome> PendingWelcomes { get; set; }
    public DbSet<MlsCommit> MlsCommits { get; set; }
    public DbSet<MlsGroupGeneration> MlsGroupGenerations { get; set; }
    public DbSet<MlsJoinRequest> MlsJoinRequests { get; set; }
    public DbSet<MlsJoinRequestApproval> MlsJoinRequestApprovals { get; set; }
    public DbSet<MlsAdmissionChallenge> MlsAdmissionChallenges { get; set; }
    public DbSet<Attachment> Attachments { get; set; }
    
    public DbSet<Message> Messages { get; set; }
    public DbSet<MinimalAttachment> MinimalAttachments { get; set; }
    public DbSet<Reaction> Reactions { get; set; }
    public DbSet<MessageSearchEntry> MessageSearchEntries { get; set; }
    public DbSet<UserMention> UserMentions { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var env = Env.Database;

        optionsBuilder.UseNpgsql(env.ConnectionString(), options =>
        {
            options.MapEnum<ChannelEncryptionState>();
            options.MapEnum<MessageType>();
            options.MapEnum<AttachmentState>();
            options.MapEnum<AuthorIdType>();
            options.MapEnum<MessagePartType>();
            options.MapEnum<MessageEncryptionState>();
            options.MapEnum<MlsGenerationState>();
            options.MapEnum<MlsJoinRequestState>();
        }).UseSnakeCaseNamingConvention();
    }
    
    public MicroserviceContext(DbContextOptions<MicroserviceContext> options) : base(options)
    {
        
    }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {

        modelBuilder.Entity<MinimalAttachment>(minimalAttachments =>
        {
            // foreign keys are shadowned here, since they don't exist in scylla and honestly.. no bock in changing the scylla ctx
            minimalAttachments.HasOne<Message>()
                .WithMany(x => x.Attachments)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Message>(messageBuilder =>
        {
            messageBuilder.HasIndex(x => x.ConversationId);
            messageBuilder.HasIndex(x => x.AuthorId);
            messageBuilder.HasIndex(x => x.ContextId);
            messageBuilder.HasIndex(x => x.ChannelId);
            messageBuilder.HasIndex(x => new { x.ContextId, x.IsPinned });

        });

        modelBuilder.Entity<UserMention>(mentionBuilder =>
        {
            // Mirrors the Scylla primary key: partitioned by user, one row per (user, message).
            mentionBuilder.HasKey(m => new { m.UserId, m.MessageId });

            // The page read: one user's mentions, newest first.
            mentionBuilder.HasIndex(m => new { m.UserId, m.CreatedAt })
                .HasDatabaseName("IX_user_mentions_user_created");

            // Postgres has no per-row TTL, so retention is a sweep - which orders by age across
            // every user at once.
            mentionBuilder.HasIndex(m => m.CreatedAt)
                .HasDatabaseName("IX_user_mentions_created");
        });

        modelBuilder.Entity<MessageSearchEntry>(searchBuilder =>
        {
            searchBuilder.HasIndex(x => x.ChannelId);
            searchBuilder.HasIndex(x => x.ConversationId);

            // NpgsqlTsVector isn't a type the InMemory provider (used by the unit test suite,
            // see TestMessagingContext) understands at all - ignoring it there is fine since
            // nothing in-memory needs full-text search; Postgres still gets the real generated
            // column + GIN index.
            if (Database.IsNpgsql())
            {
                searchBuilder.HasGeneratedTsVectorColumn(
                        x => x.SearchVector, "english", x => x.Content)
                    .HasIndex(x => x.SearchVector)
                    .HasMethod("GIN");
            }
            else
            {
                searchBuilder.Ignore(x => x.SearchVector);
            }
        });

        modelBuilder.Entity<Reaction>(reactionBuilder =>
        {
            reactionBuilder.HasKey(r => new { r.MessageId, r.UserId, r.Emoji });

            reactionBuilder.HasOne<Message>()
                .WithMany()
                .HasForeignKey(r => r.MessageId)
                .OnDelete(DeleteBehavior.Cascade);

            reactionBuilder.HasIndex(r => r.MessageId);
        });
        
        modelBuilder.Entity<Conversation>(conversationBuilder =>
        {
            
        });

        modelBuilder.Entity<PendingWelcome>(pendingWelcomeBuilder =>
        {
            pendingWelcomeBuilder
                .HasOne<Conversation>()
                .WithMany()
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            pendingWelcomeBuilder.HasIndex(w => w.UserId);

            // The fetch is always "what is waiting for *this* device" - a user's other devices must
            // never be able to drain a Welcome addressed to a leaf they do not hold.
            pendingWelcomeBuilder.HasIndex(w => new { w.UserId, w.DeviceId, w.ConsumedAt });
            pendingWelcomeBuilder.HasIndex(w => w.ContextId);
        });

        modelBuilder.Entity<MlsCommit>(commitBuilder =>
        {
            commitBuilder
                .HasOne<Conversation>()
                .WithMany()
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            // The dedup/fork guard: two members committing concurrently both target the same next
            // epoch, and exactly one insert survives.
            if (Database.IsNpgsql())
            {
                commitBuilder.HasIndex(c => new { c.ContextId, c.Generation, c.Epoch })
                    .IsUnique()
                    .HasFilter("is_proposal = false");
            }
            else
            {
                commitBuilder.HasIndex(c => new { c.ContextId, c.Generation, c.Epoch });
            }
        });

        modelBuilder.Entity<MlsAdmissionChallenge>(challengeBuilder =>
        {
            challengeBuilder
                .HasOne(c => c.JoinRequest)
                .WithMany()
                .HasForeignKey(c => c.JoinRequestId)
                .OnDelete(DeleteBehavior.Cascade);

            // Both reads are "the live challenge for this request": one to answer it, one to verify
            // the answer.
            challengeBuilder.HasIndex(c => new { c.JoinRequestId, c.ExpiresAt });
        });

        modelBuilder.Entity<MlsGroupGeneration>(generationBuilder =>
        {
            generationBuilder
                .HasOne<Conversation>()
                .WithMany()
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            generationBuilder.HasIndex(g => new { g.ContextId, g.Generation }).IsUnique();

            // At most one Active generation per context.
            if (Database.IsNpgsql())
            {
                generationBuilder.HasIndex(g => g.ContextId)
                    .IsUnique()
                    .HasFilter("state = 'active'");
            }
        });

        modelBuilder.Entity<MlsJoinRequest>(joinRequestBuilder =>
        {
            joinRequestBuilder
                .HasOne<Conversation>()
                .WithMany()
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            // The review queue read: "what is waiting on this group right now".
            joinRequestBuilder.HasIndex(r => new { r.ContextId, r.Generation, r.State });
            joinRequestBuilder.HasIndex(r => r.RequesterUserId);

            // One live request per device per era.
            if (Database.IsNpgsql())
            {
                joinRequestBuilder
                    .HasIndex(r => new { r.ContextId, r.Generation, r.RequesterDeviceId })
                    .IsUnique()
                    .HasFilter("state = 'pending'");
            }
        });

        modelBuilder.Entity<MlsJoinRequestApproval>(approvalBuilder =>
        {
            approvalBuilder
                .HasOne(a => a.JoinRequest)
                .WithMany(r => r.Approvals)
                .HasForeignKey(a => a.JoinRequestId)
                .OnDelete(DeleteBehavior.Cascade);

            // One vote per person.
            approvalBuilder.HasIndex(a => new { a.JoinRequestId, a.ApproverUserId }).IsUnique();
        });
        
        modelBuilder.Entity<ConversationMember>(memberBuilder =>
        {
            memberBuilder.HasOne(x => x.Conversation)
                .WithMany(x => x.Members).HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ConversationMemberDevice>(memberDeviceBuilder =>
        {
            memberDeviceBuilder.HasOne(x => x.ConversationMember)
                .WithMany(x => x.Devices).HasForeignKey(x => x.ConversationMemberId)
                .OnDelete(DeleteBehavior.Cascade);
            
            // Unique per membership, not globally.
            memberDeviceBuilder.HasIndex(x => new { x.ConversationMemberId, x.DeviceId }).IsUnique();
            memberDeviceBuilder.HasIndex(x => x.DeviceId);
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