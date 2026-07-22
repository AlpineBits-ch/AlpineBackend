using Domain;
using Persistence;

namespace Isle.Domain.Aggregates;

public enum FriendRequestStatus
{
    Pending,
    Accepted,
    Rejected
}

public class FriendRequest : Aggregate<FriendRequest>, IPrefixedEntity
{
    public static string Prefix { get; } = "freq";

    /// <summary>Maximum growth (0..1) a dino may have to send or accept a friend teleport.</summary>
    public const double MaxGrowthForTeleport = 0.35;

    /// <summary>How long after spawning a player may send or accept a friend teleport.</summary>
    public static readonly TimeSpan SpawnWindow = TimeSpan.FromMinutes(5);

    public string SenderPlayerId { get; set; }
    public virtual Player SenderPlayer { get; set; }

    public string ReceiverPlayerId { get; set; }
    public virtual Player ReceiverPlayer { get; set; }

    public FriendRequestStatus Status { get; set; } = FriendRequestStatus.Pending;

    public static FriendRequest Create(string senderPlayerId, string receiverPlayerId)
    {
        var id = GenerateId();
        var date = DateTimeOffset.UtcNow;
        return new FriendRequest
        {
            Id = id,
            CreatedAt = date,
            UpdatedAt = date,
            SenderPlayerId = senderPlayerId,
            ReceiverPlayerId = receiverPlayerId,
            Status = FriendRequestStatus.Pending
        };
    }

    public void Accept() => Status = FriendRequestStatus.Accepted;

    public void Reject() => Status = FriendRequestStatus.Rejected;
}
