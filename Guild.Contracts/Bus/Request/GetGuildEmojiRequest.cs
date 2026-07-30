namespace Guild.Contracts.Bus.Request;

/// <summary>Resolves+validates a custom emoji by id, scoped to whichever guild owns ChannelId -
/// used by Messaging when a reaction (or, later, a message) references a custom emoji, since
/// custom emoji are Guild-owned data Messaging has no direct database access to.</summary>
public class GetGuildEmojiRequest
{
    public string ChannelId { get; set; }
    public string EmojiId { get; set; }
}
