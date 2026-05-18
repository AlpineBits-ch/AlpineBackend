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
    public int MentionCount { get; set; }
    public static string Prefix { get; } = "reta";
}