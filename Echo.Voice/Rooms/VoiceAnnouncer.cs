using Echo.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace Echo.Voice.Rooms;

/// <summary>The only thing that may push a voice event to a client.</summary>
public sealed class VoiceAnnouncer(IHubContext<EchoRealtimeHub> hub)
{
    /// <summary>Asks a client to refetch the snapshot.</summary>
    public const string ResyncEvent = "Resync";

    /// <summary>Carries a full snapshot to a client that has fallen behind or has just arrived.</summary>
    public const string SnapshotEvent = "Snapshot";

    private static string Prefix(string kind) =>
        kind == VoiceRoomKind.Call ? "call." : "guild.voice.";

    private static string RoomIdField(string kind) =>
        kind == VoiceRoomKind.Call ? "callId" : "channelId";

    /// <summary>
    /// Builds the outgoing payload: the caller's fields, plus the room id and version.
    /// </summary>
    private static Dictionary<string, object?> Envelope(VoiceRoom room, object? payload)
    {
        var envelope = new Dictionary<string, object?>();

        if (payload is not null)
        {
            foreach (var prop in payload.GetType().GetProperties())
                envelope[prop.Name] = prop.GetValue(payload);
        }

        // Written last so a caller's payload can never contradict the room it is being sent about,
        // nor forge a version.
        envelope[RoomIdField(room.Kind)] = room.RoomId;
        envelope["instanceId"] = room.InstanceId;
        envelope["version"] = room.Version;
        return envelope;
    }

    public Task ToOthersAsync(VoiceRoom room, string exceptUserId, string eventName, object? payload = null,
        CancellationToken ct = default) =>
        hub.Clients.Users(room.OtherUserIds(exceptUserId))
            .SendAsync(Prefix(room.Kind) + eventName, Envelope(room, payload), ct);

    public Task ToAllAsync(VoiceRoom room, string eventName, object? payload = null,
        CancellationToken ct = default) =>
        hub.Clients.Users(room.AllUserIds())
            .SendAsync(Prefix(room.Kind) + eventName, Envelope(room, payload), ct);

    public Task ToUserAsync(VoiceRoom room, string userId, string eventName, object? payload = null,
        CancellationToken ct = default) =>
        hub.Clients.User(userId)
            .SendAsync(Prefix(room.Kind) + eventName, Envelope(room, payload), ct);

    /// <summary>Pushes the authoritative state to one client.</summary>
    public Task SendSnapshotAsync(
        VoiceRoom room, string userId, VoiceSubscriptionPlan? plan = null, CancellationToken ct = default) =>
        hub.Clients.User(userId).SendAsync(
            Prefix(room.Kind) + SnapshotEvent, VoiceRoomSnapshot.From(room, plan, userId), ct);

    /// <summary>Tells each participant what they should now be pulling.</summary>
    public async Task SendSubscriptionsAsync(
        VoiceRoom room, VoiceSubscriptionPlan plan, CancellationToken ct = default)
    {
        foreach (var participant in room.Participants)
        {
            var set = plan.For(participant.UserId);
            await ToUserAsync(room, participant.UserId, VoiceEvents.SubscriptionsChanged, new
            {
                mode = plan.Mode,
                revision = plan.Revision,
                activeSpeakers = plan.ActiveSpeakers,
                // Named to match VoiceSubscriptionSnapshot's own field, so the same object read
                // off a snapshot and off this event is parsed by one piece of client code - and
                // null rather than empty when no plan is in force, for the same reason the
                // snapshot omits the block. See VoiceSubscriptionPlan.WireTracksFor.
                tracks = plan.WireTracksFor(participant.UserId),
            }, ct);
        }
    }

    /// <summary>Tells a client its room is gone and it should rejoin from scratch.</summary>
    public Task SendRoomGoneAsync(VoiceRoomKey key, string userId, CancellationToken ct = default) =>
        hub.Clients.User(userId).SendAsync(
            Prefix(key.Kind) + ResyncEvent,
            new Dictionary<string, object?>
            {
                [RoomIdField(key.Kind)] = key.Id,
                // Blank rather than invented: there is no incarnation to track, and a client that
                // stored a made-up one would believe it was in sync with a room that is gone.
                ["instanceId"] = string.Empty,
                ["version"] = 0L,
                ["reason"] = "roomGone",
            }, ct);
}
