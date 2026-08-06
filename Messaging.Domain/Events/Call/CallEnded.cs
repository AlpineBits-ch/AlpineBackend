using Domain;
using Messaging.Domain.Enums;

namespace Messaging.Domain.Events.Call;

public class CallEnded : DomainEvent
{
    public string CallId { get; set; }
    public CallEndReason Reason { get; set; }

    /// <summary>The conversation this call belonged to, empty when it belonged to none.</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>Every user invited to the call, answered or not.</summary>
    public List<string> ParticipantIds { get; set; } = [];

    public string CreatorId { get; set; } = string.Empty;

    /// <summary>When the call was placed - the start of the duration a client renders.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Whether anyone other than the creator ever connected.</summary>
    public bool Answered { get; set; }
}
