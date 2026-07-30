namespace Messaging.Domain.Entities;

/// <summary>Denormalized Scylla-only lookup row backing "list pinned messages" - see
/// Message.IsPinned/PinnedAt/PinnedById for the source of truth on the message row itself.</summary>
public class PinnedMessage
{
    public string ContextId { get; set; }
    public string MessageId { get; set; }
    public DateTime PinnedAt { get; set; }
    public string PinnedById { get; set; }
}
