namespace Messaging.Application.Dtos.Response;

public class RemoveReactionDto
{
    public string Reaction { get; set; }
    public string ContextId { get; set; }

    /// <summary>Set when the reaction being removed is on a guild channel message - needed (in
    /// addition to ContextId, which the repository lookup uses) so the removal can fan out to
    /// Guild.Application for the guild.ReactionRemoved broadcast, same as ContextId being a
    /// channel id vs a conversation id isn't otherwise distinguishable here.</summary>
    public string? ChannelId { get; set; }
    public string? ConversationId { get; set; }
}