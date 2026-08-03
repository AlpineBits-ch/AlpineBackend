using System.ComponentModel.DataAnnotations.Schema;
using Domain;
using FluentValidation;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Events.Channel;
using Guild.Domain.Validators;
using Persistence;

namespace Guild.Domain.Aggregates;

public class CreateChannelParams
{
    public string Name { get; init; }
    public string Description { get; init; }
    public ChannelType Type { get; init; } = ChannelType.Text;
    public string? CategoryId { get; init; }
    public string GuildId { get; init; }
    public int Position { get; init; } = 0;
    public string? ParentChannelId { get; init; }
    public string? CreatedByUserId { get; init; }
}

public class Channel : Aggregate<Channel>, IPrefixedEntity
{
    public required string Name { get; set; }
    public  string? Description { get; set; }
    public required ChannelType Type { get; set; } = ChannelType.Text;
    
    public string? CategoryId { get; set; }
    public virtual Category? Category { get; set; }
    public string GuildId { get; set; }
    public virtual Guild Guild { get; set; } = null!;
    
    // Inbound navigation property
    public virtual Guild? SystemChannelGuild { get; set; }
    
    public bool IsAgeRestricted { get; set; }
    public bool IsPrivate { get; set; }
    public int Position { get; set; } = 0;
    public int SlowModeSeconds { get; set; } = 0;

    /// <summary>Set only for Type == Thread; the channel it was created under.</summary>
    public string? ParentChannelId { get; set; }
    public virtual Channel? ParentChannel { get; set; }

    /// <summary>Set only for Type == Thread: who created it, used to gate ManageOwnThreads
    /// (creator) vs ManageAnyThread (moderator) on the archive endpoint.</summary>
    public string? CreatedByUserId { get; set; }
    public bool IsArchived { get; set; }

    // ── Forum post state (Type == Thread under a Forum/Media parent) ─────────────────────────
    // These live on Channel rather than in a side table because every one of them is either a
    // sort key or a filter on the forum post listing - a join there would cost more than the
    // columns do on the ~all-null rows of every other channel type.

    /// <summary>Pinned posts sort above unpinned ones within the forum's ordering, so they land at
    /// the top of page one. Distinct from pinning a *message inside* a post, which Messaging owns.</summary>
    public bool IsPinned { get; set; }

    /// <summary>Moderator-imposed "no new messages".</summary>
    public bool IsLocked { get; set; }

    /// <summary>
    /// Timestamp of the most recent message, maintained by MessageCreatedHandler from the message's
    /// own stored CreatedAt.
    /// </summary>
    public DateTimeOffset? LastActivityAt { get; set; }

    /// <summary>Denormalized message count.</summary>
    public int MessageCount { get; set; }

    /// <summary>
    /// Id of the most recent message, for handing back to Messaging as an <c>after</c> cursor when
    /// fetching unread previews.
    /// </summary>
    public string? LastMessageId { get; set; }

    /// <summary>When this post auto-archives absent further activity; pushed forward by each new
    /// message. Honoured by a periodic sweep, so the flip can lag the timestamp by minutes.</summary>
    public DateTimeOffset? AutoArchiveAt { get; set; }

    /// <summary>
    /// The post's auto-archive window, snapshotted from the forum config at creation.
    /// </summary>
    public int? AutoArchiveMinutes { get; set; }

    public ICollection<ChannelPermission> Permissions { get; set; } = [];
    
    public virtual ICollection<ReadState> ReadStates { get; set; } = [];
    
    public virtual ICollection<WebhookConfig> WebhookConfigs { get; set; } = [];
    
    [NotMapped] public static string Prefix { get; } = "chan";

    public virtual ICollection<PublicKeyStore> PublicKeys { get; set; } = [];

    public EncryptionState EncryptionState { get; init; } = EncryptionState.Plain;

    public static Channel Create(CreateChannelParams @params)
    {
        var id = GenerateId();
        
        var date = DateTime.UtcNow;
        var channel = new Channel()
        {
            Id = id,
            CreatedAt = date,
            UpdatedAt = date,
            Name = @params.Name,
            Description = @params.Description,
            Type = @params.Type,
            CategoryId = @params.CategoryId,
            GuildId = @params.GuildId,
            Position = @params.Position,
            ParentChannelId = @params.ParentChannelId,
            CreatedByUserId = @params.CreatedByUserId,
        };

        channel.AddDomainEvent(new ChannelCreated() { ChannelId = id, GuildId = @params.GuildId });

        new ChannelValidator().ValidateAndThrow(channel);

        return channel;
    }

    public class UpdateChannelParams
    {
        public string Name { get; init; } = null!;
        public string? Description { get; init; }
        public bool IsAgeRestricted { get; init; }
        public bool IsPrivate { get; init; }
        public int SlowModeSeconds { get; init; }
    }

    public void Update(UpdateChannelParams @params)
    {
        Name = @params.Name;
        Description = @params.Description;
        IsAgeRestricted = @params.IsAgeRestricted;
        IsPrivate = @params.IsPrivate;
        SlowModeSeconds = @params.SlowModeSeconds;

        new ChannelValidator().ValidateAndThrow(this);

        AddDomainEvent(new ChannelUpdated { ChannelId = Id, GuildId = GuildId });
    }

}