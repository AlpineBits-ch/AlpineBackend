namespace Messaging.Application.Dtos.Request;

public class CreateReactionDto
{
    public string ConversationId { get; set; }
    public string Reaction { get; set; }

    public string? ChannelId { get; set; }

    /// <summary>Guild custom emoji id - omit for a unicode reaction. Only valid alongside ChannelId
    /// (custom emoji are guild-scoped, not usable in DMs in v1).</summary>
    public string? EmojiId { get; set; }
}