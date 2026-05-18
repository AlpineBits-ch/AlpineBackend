using Messaging.Domain.Aggregates;
using Persistence;

namespace Messaging.Domain.Entities;

public class PendingWelcome : BaseEntity<PendingWelcome>, IPrefixedEntity
{
    public string ConversationId { get; set; }
    public string UserId { get; set; }
    public string DeviceId { get; set; }
    public byte[] Welcome { get; set; }

    public static string Prefix { get; } = "pewe";
}