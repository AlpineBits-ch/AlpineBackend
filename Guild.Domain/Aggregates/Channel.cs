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
    public string? StarterMessageId { get; init; }
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

    /// <summary>Lucide icon name in kebab-case, or null to fall back to the channel type's own icon.</summary>
    public string? Icon { get; set; }

    /// <summary>Hex colour as #RRGGBB, or null to fall back to the uniform default colour.</summary>
    public string? IconColor { get; set; }

    /// <summary>Set only for a thread-shaped type (Thread, Scene); the channel it was created
    /// under.</summary>
    public string? ParentChannelId { get; set; }
    public virtual Channel? ParentChannel { get; set; }

    /// <summary>Set only for a thread-shaped type (Thread, Scene): who created it, used to gate
    /// ManageOwnThreads (creator) vs ManageAnyThread (moderator) on the archive endpoint.</summary>
    public string? CreatedByUserId { get; set; }

    /// <summary>Set only for a thread started from an existing message; the message it hangs off,
    /// which stays in the parent channel and renders a thread card there. Unique where present, so
    /// two people racing the same button cannot give one message two threads.</summary>
    public string? StarterMessageId { get; set; }

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
            StarterMessageId = @params.StarterMessageId,
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
        public int SlowModeSeconds { get; init; }

        // Absolute values; the endpoint has already resolved the clear sentinel.
        public string? Icon { get; init; }
        public string? IconColor { get; init; }
    }

    /// <summary>
    /// IsPrivate is not settable here: it is only meaningful alongside the @everyone ViewChannel
    /// overwrite that enforces it, so ChannelPrivacyService owns both halves.
    /// </summary>
    public void Update(UpdateChannelParams @params)
    {
        Name = @params.Name;
        Description = @params.Description;
        IsAgeRestricted = @params.IsAgeRestricted;
        SlowModeSeconds = @params.SlowModeSeconds;
        Icon = @params.Icon;
        IconColor = @params.IconColor;

        new ChannelValidator().ValidateAndThrow(this);

        AddDomainEvent(new ChannelUpdated { ChannelId = Id, GuildId = GuildId });
    }

}