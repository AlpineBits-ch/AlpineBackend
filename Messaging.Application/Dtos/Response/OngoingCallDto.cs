namespace Messaging.Application.Dtos.Response;

/// <summary>
/// A call in progress, as described to a member of its conversation who is not (yet) in it.
/// </summary>
public record OngoingCallDto(
    string CallId,
    string ConversationId,
    string Status,
    string CreatorId,
    DateTimeOffset StartedAt,
    List<string> ConnectedUserIds);
