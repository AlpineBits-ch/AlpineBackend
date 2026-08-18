using Messaging.Domain.Entities;

namespace Messaging.Application.Dtos.Response;

/// <summary>One stored draft, as its author reads it back.</summary>
/// <param name="ContextId">The channel or conversation it belongs to.</param>
/// <param name="ChannelId">Set for a channel draft, null otherwise.</param>
/// <param name="ConversationId">Set for a conversation draft, null otherwise.</param>
/// <param name="Content">The body as it was last saved.</param>
/// <param name="InReplyTo">The message being replied to, or null.</param>
/// <param name="UpdatedAt">When the body was last written.</param>
/// <param name="DeviceId">Which device wrote it, so a client can recognise its own echo.</param>
public sealed record MessageDraftDto(
    string ContextId,
    string? ChannelId,
    string? ConversationId,
    string Content,
    string? InReplyTo,
    DateTimeOffset UpdatedAt,
    string? DeviceId = null)
{
    /// <summary>The wire form of a stored draft.</summary>
    /// <param name="draft">The row.</param>
    /// <param name="deviceId">The device that wrote it, when this is answering that write.</param>
    /// <returns>The DTO.</returns>
    public static MessageDraftDto From(MessageDraft draft, string? deviceId = null) => new(
        draft.ContextId,
        draft.ChannelId,
        draft.ConversationId,
        draft.Content,
        draft.InReplyTo,
        draft.UpdatedAt,
        deviceId);
}
