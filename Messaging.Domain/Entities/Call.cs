using Domain;
using Messaging.Domain.Enums;
using Messaging.Domain.Events.Call;
using Persistence;

namespace Messaging.Domain.Entities;

public class CallTracks
{
    public required string TrackId { get; set; }
    public required string UserId { get; set; }
    public CallTrackStatus Status { get; set; } = CallTrackStatus.Ongoing;
}

public class CallParticipant
{
    public string UserId { get; set; }
    public DateTime JoinedAt { get; set; }
    public CallStatus Status { get; set; } = CallStatus.Pending;
    public string? CfSessionId { get; set; }
    public string? AudioTrackName { get; set; }   
}

public class Call : Aggregate<Call>, IPrefixedEntity
{
    public static string GetCacheId(string id)
    {
        return $"{Prefix}:{id}";
    }
    public string ConversationId { get; set; }  // add this
    public string CreatorId { get; set; }
    public static string Prefix { get; } = "call";
    public CallStatus Status { get; set; } = CallStatus.Pending;
    public ICollection<CallTracks> Tracks { get; set; } = [];
    
    public ICollection<CallParticipant> Participants { get; set; } = [];

    public void MarkCreated()
    {
        AddDomainEvent(new CallCreated
        {
            CallId = this.Id,
        });
    }

    public void Accept(string userId)
    {
        var participant = Participants.FirstOrDefault(p => p.UserId == userId);
        if(participant is null) return;
        participant.Status = CallStatus.Connected;
        this.Status = CallStatus.Connected;
        this.AddDomainEvent(new CallAccepted()
        {
            CallId = this.Id,
            UserId = userId,
        });
    }
    
    public bool IsParticipant(string userId) => Participants.Any(p => p.UserId == userId);
    public bool IsCreator(string userId) => CreatorId == userId;
    
    public void Decline(string userId)
    {
        var creator = Participants.FirstOrDefault(p => p.UserId == CreatorId)!;
        var participant = Participants.FirstOrDefault(p => p.UserId == userId);
        if(participant is null) return;
        participant.Status = CallStatus.Rejected;

        AddDomainEvent(new CallDeclined()
        {
            CallId = this.Id,
            UserId = userId,
        });

        if (Participants.Count == 2)
        {
            this.Status = CallStatus.Rejected;
            return;
        }

        if(Participants.Except([creator]).All(p => p.Status == CallStatus.Rejected)) this.Status = CallStatus.Rejected;
    }
    

    public void End(string userId)
    {
        this.Status = CallStatus.Completed;

        AddDomainEvent(new CallEnded()
        {
            CallId = this.Id,
        });
    }

    /// <summary>Auto-declines the call if nobody has answered by the ring timeout. Guarded by
    /// the Pending check since the scheduled check can land after the call was already
    /// accepted/declined/ended through the normal paths.</summary>
    public void Timeout()
    {
        if (Status != CallStatus.Pending) return;
        this.Status = CallStatus.Rejected;

        AddDomainEvent(new CallEnded()
        {
            CallId = this.Id,
        });
    }
}