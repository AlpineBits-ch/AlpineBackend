using Guild.Domain.Enums;
using Persistence;

namespace Guild.Domain.Entity;

public class CreateGuildScheduledEventParams
{
    public string GuildId { get; set; } = null!;
    public string CreatorUserId { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public string? Location { get; set; }
    public string? VoiceChannelId { get; set; }
}

public class GuildScheduledEvent : BaseEntity<GuildScheduledEvent>, IPrefixedEntity
{
    public static string Prefix { get; } = "sevt";

    public string GuildId { get; set; } = null!;
    public virtual Aggregates.Guild Guild { get; set; } = null!;

    public string CreatorUserId { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }

    /// <summary>Freeform text location ("the park", "our Discord... I mean Venta"). Mutually
    /// exclusive in practice with VoiceChannelId but not enforced as a DB constraint - a client
    /// sending both just means Location is treated as supplementary notes.</summary>
    public string? Location { get; set; }
    public string? VoiceChannelId { get; set; }
    public virtual Aggregates.Channel? VoiceChannel { get; set; }

    public GuildScheduledEventStatus Status { get; set; } = GuildScheduledEventStatus.Scheduled;

    public virtual ICollection<GuildScheduledEventInterest> Interested { get; set; } = [];

    public static GuildScheduledEvent Create(CreateGuildScheduledEventParams parameters)
    {
        var date = DateTime.UtcNow;
        return new GuildScheduledEvent
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            GuildId = parameters.GuildId,
            CreatorUserId = parameters.CreatorUserId,
            Title = parameters.Title,
            Description = parameters.Description,
            StartsAt = parameters.StartsAt,
            EndsAt = parameters.EndsAt,
            Location = parameters.Location,
            VoiceChannelId = parameters.VoiceChannelId,
            Status = GuildScheduledEventStatus.Scheduled,
        };
    }
}

public class GuildScheduledEventInterest
{
    public string EventId { get; set; } = null!;
    public virtual GuildScheduledEvent Event { get; set; } = null!;
    public string UserId { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}
