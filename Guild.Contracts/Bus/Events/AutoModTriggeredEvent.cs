namespace Guild.Contracts.Bus.Events;

/// <summary>Published by Messaging (fire-and-forget) when a message is blocked by auto-moderation
/// - Guild.Application resolves GuildId (same as it does for message/reaction/pin events) and
/// writes an audit log entry; there's no realtime broadcast, since this is moderator-facing
/// history, not something other members should see live.</summary>
public class AutoModTriggeredEvent
{
    public string ChannelId { get; set; }
    public string UserId { get; set; }
    public string Reason { get; set; } // "blocked_word" | "rate_limited"
}
