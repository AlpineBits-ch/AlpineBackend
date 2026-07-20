using Isle.Domain.Aggregates;
using Persistence;

namespace Isle.Domain.Entity;

public class CreateInviteParams
{
    public string ServerId { get; set; }
    public string SenderPlayerId { get; set; }
    public string ReceiverPlayerId { get; set; }
}

public class Invite : BaseEntity<Invite>, IPrefixedEntity
{
    public static string Prefix { get; } = "invite";
    public virtual IsleServer Server { get; set; }
    public string ServerId { get; set; }
    public virtual Player SenderPlayer { get; set; }
    public string SenderPlayerId { get; set; }
    public virtual Player ReceiverPlayer { get; set; }
    public string ReceiverPlayerId { get; set; }
    
    public int FriendlyId { get; set; } = Random.Shared.Next(5000);

    public static Invite Create(CreateInviteParams parameters)
    {
        var id = Invite.GenerateId();
        var date = DateTime.UtcNow;

        return new Invite()
        {
            Id = id,
            CreatedAt = date,
            UpdatedAt = date,
            ServerId = parameters.ServerId,
            SenderPlayerId = parameters.SenderPlayerId,
            ReceiverPlayerId = parameters.ReceiverPlayerId,
        };
    }
}