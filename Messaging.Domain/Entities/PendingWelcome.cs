using Messaging.Domain.Aggregates;
using Persistence;

namespace Messaging.Domain.Entities;

public class CreatePendingWelcomeParams
{
    public string ContextId { get; init; } = null!;
    public string? ConversationId { get; init; }
    public string? ChannelId { get; init; }
    public string UserId { get; init; } = null!;
    public string DeviceId { get; init; } = null!;
    public byte[] Welcome { get; init; } = null!;
    public int Generation { get; init; }
    public long Epoch { get; init; }
}

/// <summary>
/// An MLS Welcome parked for one specific device of one specific user until that device is online,
/// fetches it, successfully joins the group, and acknowledges it.
///
/// <para><b>Acknowledgement is explicit, not implicit-on-read.</b> A Welcome is the only way into a
/// group: if it is handed out and dropped - the fetch succeeds but the join throws, or the process
/// dies in between - the conversation becomes permanently undecryptable for that device, because
/// the Welcome's init key is single-use and the group has already moved on. So reading does not
/// consume; <see cref="ConsumedAt"/> is stamped only once the client confirms the join. Re-reads
/// before that are idempotent and hand back the same row.</para>
/// </summary>
public class PendingWelcome : BaseEntity<PendingWelcome>, IPrefixedEntity
{
    public static string Prefix { get; } = "pewe";

    /// <summary>Conversation id or channel id - the MLS group this Welcome admits the device to.</summary>
    public string ContextId { get; set; } = null!;

    /// <summary>Set when the group is a conversation; drives cascade delete.</summary>
    public string? ConversationId { get; set; }

    /// <summary>Set when the group is a guild channel. No FK - channels live in the Guild service.</summary>
    public string? ChannelId { get; set; }

    public string UserId { get; set; } = null!;

    /// <summary>Client device id. A Welcome is addressed to one device, never to a user - every
    /// device holds its own leaf and its own init key.</summary>
    public string DeviceId { get; set; } = null!;

    public byte[] Welcome { get; set; } = null!;

    /// <summary>Which <see cref="MlsGroupGeneration"/> of this context the Welcome admits the device
    /// to. A Welcome minted before encryption was toggled off is worthless afterwards, and the
    /// generation is what lets a client tell that without trying the join.</summary>
    public int Generation { get; set; }

    /// <summary>Group epoch the joining device lands on, so it knows which commits to catch up from.</summary>
    public long Epoch { get; set; }

    /// <summary>Null until the device confirms it joined the group. See the type remarks.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    public static PendingWelcome Create(CreatePendingWelcomeParams parameters)
    {
        var date = DateTimeOffset.UtcNow;
        return new PendingWelcome
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            ContextId = parameters.ContextId,
            ConversationId = parameters.ConversationId,
            ChannelId = parameters.ChannelId,
            UserId = parameters.UserId,
            DeviceId = parameters.DeviceId,
            Welcome = parameters.Welcome,
            Generation = parameters.Generation,
            Epoch = parameters.Epoch,
        };
    }
}
