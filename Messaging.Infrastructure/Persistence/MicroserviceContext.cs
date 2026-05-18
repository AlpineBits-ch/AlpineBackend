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
    public DbSet<Attachment> Attachments { get; set; }
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        var env = Env.Database;

        optionsBuilder.UseNpgsql(env.ConnectionString(), options =>
        {
            options.MapEnum<ChannelEncryptionState>();
            options.MapEnum<MessageType>();
            options.MapEnum<AttachmentState>();
        }).UseSnakeCaseNamingConvention();
    }
    
    public MicroserviceContext(DbContextOptions<MicroserviceContext> options) : base(options)
    {
        
    }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
       
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
            
            memberDeviceBuilder.HasIndex(x => x.DeviceId).IsUnique();
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