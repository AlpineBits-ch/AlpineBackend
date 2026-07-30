namespace Guild.Contracts.Bus.Events;

/// <summary>
/// Republished by ReactionHandler once it has resolved the GuildId the raw ReactionCreatedEvent/
/// ReactionRemovedEvent don't carry - mirrors MessageCreatedForBots's exact reasoning.
/// </summary>
public class ReactionCreatedForBots
{
    public string GuildId { get; set; }
    public string ChannelId { get; set; }
    public string MessageId { get; set; }
    public string Emoji { get; set; }
    public string UserId { get; set; }
    public string? EmojiId { get; set; }
}

public class ReactionRemovedForBots
{
    public string GuildId { get; set; }
    public string ChannelId { get; set; }
    public string MessageId { get; set; }
    public string Emoji { get; set; }
    public string UserId { get; set; }
}
