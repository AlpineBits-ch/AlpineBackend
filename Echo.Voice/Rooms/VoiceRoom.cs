using System.Text.Json.Serialization;

namespace Echo.Voice.Rooms;

/// <summary>What kind of room this is.</summary>
public static class VoiceRoomKind
{
    public const string Channel = "channel";
    public const string Call = "call";
}

/// <summary>Identifies one voice room across both deployments.</summary>
public readonly record struct VoiceRoomKey(string Kind, string Id)
{
    public static VoiceRoomKey Channel(string channelId) => new(VoiceRoomKind.Channel, channelId);
    public static VoiceRoomKey Call(string callId) => new(VoiceRoomKind.Call, callId);

    /// <summary>Where the room blob lives.</summary>
    public string CacheKey => $"voice:room:{Kind}:{Id}";

    /// <summary>Scope for <see cref="Echo.Realtime.Caching.StreamViewerStore"/>.</summary>
    public string ViewerScope => $"{Kind}:{Id}";

    public override string ToString() => $"{Kind}:{Id}";
}

/// <summary>Whether a participant's media is actually pullable by peers.</summary>
public enum VoicePublishState
{
    /// <summary>In the room, but publishing nothing peers can subscribe to yet.</summary>
    Joined,

    /// <summary>Has a session and a microphone track that peers can pull.</summary>
    Publishing,
}

public sealed class ActiveScreenShare
{
    public string ShareId { get; set; } = string.Empty;
    public List<string> TrackNames { get; set; } = [];
}

/// <summary>One participant's state in a room.</summary>
public sealed class VoiceParticipant
{
    public string UserId { get; set; } = string.Empty;

    /// <summary>The device currently connected to this room's audio.</summary>
    public string? DeviceId { get; set; }

    public string? CfSessionId { get; set; }
    public string? AudioTrackName { get; set; }

    public bool IsSelfMuted { get; set; }
    public bool IsSelfDeafened { get; set; }
    public bool IsServerMuted { get; set; }
    public bool IsServerDeafened { get; set; }
    public bool IsStreaming { get; set; }
    public List<ActiveScreenShare> ActiveScreenShares { get; set; } = [];
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Derived, never stored, and therefore impossible to set wrong.</summary>
    [JsonIgnore]
    public VoicePublishState PublishState =>
        CfSessionId is not null && AudioTrackName is not null
            ? VoicePublishState.Publishing
            : VoicePublishState.Joined;
}

/// <summary>The roster of one voice room, for both guild channels and direct calls.</summary>
public sealed class VoiceRoom
{
    public string RoomId { get; set; } = string.Empty;

    /// <summary>See <see cref="VoiceRoomKind"/>.</summary>
    public string Kind { get; set; } = VoiceRoomKind.Channel;

    /// <summary>Guild context for a channel room; null for a call, exactly as Discord leaves
    /// <c>guild_id</c> absent on a private-channel voice state.</summary>
    public string? GuildId { get; set; }

    /// <summary>Identifies this incarnation of the room.</summary>
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Monotonic, bumped by <see cref="VoiceRoomStore"/> on every mutation and stamped onto every
    /// event and snapshot.
    /// </summary>
    [JsonInclude]
    public long Version { get; internal set; }

    public List<VoiceParticipant> Participants { get; set; } = [];

    [JsonIgnore]
    public VoiceRoomKey Key => new(Kind, RoomId);

    public VoiceParticipant? Find(string userId) =>
        Participants.FirstOrDefault(p => p.UserId == userId);

    /// <summary>Everyone except <paramref name="userId"/> - the audience for a change they made.</summary>
    public List<string> OtherUserIds(string userId) =>
        Participants.Where(p => p.UserId != userId).Select(p => p.UserId).ToList();

    public List<string> AllUserIds() => Participants.Select(p => p.UserId).ToList();
}
