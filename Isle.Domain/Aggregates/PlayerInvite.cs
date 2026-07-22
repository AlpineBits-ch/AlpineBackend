using Domain;
using Persistence;

namespace Isle.Domain.Aggregates;

public enum PlayerInviteStatus
{
    Pending,
    Accepted,
    Rejected
}

public class PlayerInvite : Aggregate<PlayerInvite>, IPrefixedEntity
{
    public static string Prefix { get; } = "invite";

    /// <summary>Maximum growth (0..1) a dino may have to send or accept an invite teleport.</summary>
    public const double MaxGrowthForTeleport = 0.35;

    /// <summary>How long after spawning a player may send or accept an invite teleport.</summary>
    public static readonly TimeSpan SpawnWindow = TimeSpan.FromMinutes(5);

    public string SenderPlayerId { get; set; }
    public virtual Player SenderPlayer { get; set; }

    public string ReceiverPlayerId { get; set; }
    public virtual Player ReceiverPlayer { get; set; }

    public PlayerInviteStatus Status { get; set; } = PlayerInviteStatus.Pending;

    public static PlayerInvite Create(string senderPlayerId, string receiverPlayerId)
    {
        var id = GenerateId();
        var date = DateTimeOffset.UtcNow;
        return new PlayerInvite
        {
            Id = id,
            CreatedAt = date,
            UpdatedAt = date,
            SenderPlayerId = senderPlayerId,
            ReceiverPlayerId = receiverPlayerId,
            Status = PlayerInviteStatus.Pending
        };
    }

    public void Accept() => Status = PlayerInviteStatus.Accepted;

    public void Reject() => Status = PlayerInviteStatus.Rejected;
}
