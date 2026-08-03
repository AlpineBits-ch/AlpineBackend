using Guild.Domain.Aggregates;
using Persistence;

namespace Guild.Domain.Entity;

public class ReadState : BaseEntity<ReadState>, IPrefixedEntity
{
    
    public string MemberId { get; set; } = null!;
    public GuildMember GuildMember { get; set; } = null!;
    public string ChannelId { get; set; } = null!;
    public Channel Channel { get; set; } = null!;
    public string? LastReadMessageId { get; set; } = null!;

    /// <summary>The stored CreatedAt of the acked message.</summary>
    public DateTimeOffset? LastReadAt { get; set; }

    /// <summary>Snapshot of <see cref="Aggregates.Channel.MessageCount"/> at ack time.</summary>
    public int MessageCountAtRead { get; set; }

    // MentionCount used to live here as an incremented counter.

    public static string Prefix { get; } = "reta";
}