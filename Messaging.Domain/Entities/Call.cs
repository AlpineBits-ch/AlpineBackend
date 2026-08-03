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

    /// <summary>The device currently connected to this call's audio for this participant, if
    /// any. A user can only be actively connected from one device at a time - accepting from a
    /// second device transfers this rather than creating a second connected leg.</summary>
    public string? ActiveDeviceId { get; set; }
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

    /// <summary>Set when a Leave drops the call to exactly one connected participant; cleared
    /// once a second participant (re)connects. Drives the alone-timeout grace period.</summary>
    public DateTime? AloneSince { get; set; }

    public void MarkCreated()
    {
        AddDomainEvent(new CallCreated
        {
            CallId = this.Id,
        });
    }

    public void Accept(string userId, string deviceId)
    {
        var participant = Participants.FirstOrDefault(p => p.UserId == userId);
        if(participant is null) return;

        ConnectDevice(participant, deviceId);
        this.Status = CallStatus.Connected;

        this.AddDomainEvent(new CallAccepted()
        {
            CallId = this.Id,
            UserId = userId,
            DeviceId = deviceId,
        });
    }

    /// <summary>Marks a participant's media session as connected from a specific device.</summary>
    public void ConnectDevice(CallParticipant participant, string deviceId)
    {
        var wasConnected = participant.Status == CallStatus.Connected;
        var previousDevice = participant.ActiveDeviceId;

        participant.Status = CallStatus.Connected;
        participant.ActiveDeviceId = deviceId;

        if (wasConnected && previousDevice is not null && previousDevice != deviceId)
        {
            // Same user connecting from a second device - transfer the connection rather than
            // running two legs. The old device must tear itself down; its CF session is stale.
            AddDomainEvent(new CallDeviceTakeover
            {
                CallId = this.Id,
                UserId = participant.UserId,
                OldDeviceId = previousDevice,
                NewDeviceId = deviceId,
                OldCfSessionId = participant.CfSessionId,
                OldAudioTrackName = participant.AudioTrackName,
            });
            participant.CfSessionId = null;
            participant.AudioTrackName = null;
        }
        else if (!wasConnected)
        {
            // A genuinely new participant just connected - if the call had a lone survivor
            // waiting out the alone-timeout, this cancels it.
            this.AloneSince = null;
        }
    }

    public bool IsParticipant(string userId) => Participants.Any(p => p.UserId == userId);
    public bool IsCreator(string userId) => CreatorId == userId;

    public void Decline(string userId, string deviceId)
    {
        var participant = Participants.FirstOrDefault(p => p.UserId == userId);
        if(participant is null) return;

        if (participant.Status == CallStatus.Connected)
        {
            // Stale race: this user already answered from another device (or this same device,
            // re-sent). The call is unaffected - just tell this device to stop ringing.
            AddDomainEvent(new CallDeviceDismissed
            {
                CallId = this.Id,
                UserId = userId,
                DeviceId = deviceId,
            });
            return;
        }

        var creator = Participants.FirstOrDefault(p => p.UserId == CreatorId)!;
        participant.Status = CallStatus.Rejected;

        AddDomainEvent(new CallDeclined()
        {
            CallId = this.Id,
            UserId = userId,
            DeviceId = deviceId,
        });

        if (Participants.Count == 2)
        {
            this.Status = CallStatus.Rejected;
            // Also raise CallEnded - CallDeclined alone only reaches clients that specifically
            // handle it (group-call "so-and-so declined" UI); CallEnded is the one every client
            // already tears its ringing/in-call UI down on, so the 1:1 case needs it too.
            AddDomainEvent(new CallEnded()
            {
                CallId = this.Id,
                Reason = CallEndReason.Declined,
            });
            return;
        }

        if (Participants.Except([creator]).All(p => p.Status == CallStatus.Rejected))
        {
            this.Status = CallStatus.Rejected;
            AddDomainEvent(new CallEnded()
            {
                CallId = this.Id,
                Reason = CallEndReason.Declined,
            });
        }
    }

    /// <summary>Removes just this participant from a still-active call.</summary>
    public void Leave(string userId, string deviceId)
    {
        var participant = Participants.FirstOrDefault(p => p.UserId == userId);
        if (participant is null || participant.Status != CallStatus.Connected) return;
        if (participant.ActiveDeviceId != deviceId) return;

        participant.Status = CallStatus.Left;
        participant.ActiveDeviceId = null;

        AddDomainEvent(new CallParticipantLeft
        {
            CallId = this.Id,
            UserId = userId,
        });

        var stillConnected = Participants.Where(p => p.Status == CallStatus.Connected).ToList();
        switch (stillConnected.Count)
        {
            case 0:
                this.Status = CallStatus.Completed;
                this.AloneSince = null;
                AddDomainEvent(new CallEnded
                {
                    CallId = this.Id,
                    Reason = CallEndReason.AllParticipantsLeft,
                });
                break;
            case 1:
                this.AloneSince = DateTime.UtcNow;
                AddDomainEvent(new CallWentAlone
                {
                    CallId = this.Id,
                    UserId = stillConnected[0].UserId,
                    AloneSince = this.AloneSince.Value,
                });
                break;
        }
    }

    /// <summary>Force-terminates the call for every participant, regardless of who's still
    /// connected - used for explicit "end call for everyone" actions and system-triggered ends
    /// (ring/alone timeouts route through here too via their own reason).</summary>
    public void End(CallEndReason reason = CallEndReason.UserEnded)
    {
        this.Status = CallStatus.Completed;
        this.AloneSince = null;

        AddDomainEvent(new CallEnded()
        {
            CallId = this.Id,
            Reason = reason,
        });
    }

    /// <summary>Auto-declines the call if nobody has answered by the ring timeout.</summary>
    public void Timeout()
    {
        if (Status != CallStatus.Pending) return;
        this.Status = CallStatus.Rejected;

        AddDomainEvent(new CallEnded()
        {
            CallId = this.Id,
            Reason = CallEndReason.Declined,
        });
    }

    /// <summary>Ends the call if it's still stuck at exactly one connected participant matching
    /// the alone-timeout check that was scheduled. No-ops if someone rejoined, the sole survivor
    /// left too, or the call already ended some other way in the meantime.</summary>
    public void EndIfStillAlone(DateTime expectedAloneSince)
    {
        if (AloneSince != expectedAloneSince) return;
        if (Participants.Count(p => p.Status == CallStatus.Connected) != 1) return;
        End(CallEndReason.AloneTimeout);
    }
}