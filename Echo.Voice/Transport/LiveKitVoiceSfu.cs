using Echo.Realtime.LiveKit;
using Echo.Voice.Rooms;
using Echo.Voice.Tracks;
using Microsoft.Extensions.Logging;

namespace Echo.Voice.Transport;

/// <summary><see cref="IVoiceSfu"/> over our own LiveKit fleet.</summary>
public sealed class LiveKitVoiceSfu(
    LiveKitRoomClient client,
    LiveKitRoomRegistry registry,
    LiveKitOptions options,
    ILogger<LiveKitVoiceSfu> logger) : IVoiceSfu
{
    public string Backend => "livekit";

    public bool IsConfigured => options.IsConfigured;

    /// <summary>A room key as the SFU names it.</summary>
    public static string RoomName(VoiceRoomKey key) => $"{key.Kind}-{key.Id}";

    public async Task<VoiceConnection> ConnectAsync(
        VoiceRoomKey key,
        string identity,
        string? displayName,
        VoiceMediaRights rights,
        int? maxParticipants = null,
        CancellationToken ct = default)
    {
        Require();

        var room = RoomName(key);

        // Find-or-place and create, as one locked operation.
        var node = await Guard("CreateRoom", () => registry.PlaceAsync(
            room, RegionFor(key),
            (placed, token) => client.CreateRoomAsync(placed, room, maxParticipants, token),
            ct));

        var token = LiveKitToken.ForJoin(
            options, room, identity, displayName, GrantsFor(rights));

        return new VoiceConnection(
            Backend, node.SignalingUrl, token, room, identity,
            DateTimeOffset.UtcNow.Add(options.JoinTokenTtl));
    }

    public async Task<bool> UpdateRightsAsync(
        VoiceRoomKey key, string identity, VoiceMediaRights rights, CancellationToken ct = default)
    {
        Require();

        var room = RoomName(key);
        var node = await registry.FindAsync(room, ct);
        if (node is null) return false;

        try
        {
            await client.UpdatePermissionsAsync(node, room, identity, GrantsFor(rights), ct);
            return true;
        }
        catch (LiveKitControlException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Not in the room.
            return false;
        }
        catch (LiveKitControlException ex)
        {
            throw Translate("UpdateParticipant", ex);
        }
    }

    public async Task DisconnectAsync(
        VoiceRoomKey key, string identity, CancellationToken ct = default)
    {
        if (!IsConfigured) return;

        var room = RoomName(key);
        var node = await registry.FindAsync(room, ct);
        if (node is null) return;

        try
        {
            await client.RemoveParticipantAsync(node, room, identity, ct);
        }
        catch (LiveKitControlException ex)
        {
            // Best effort by design.
            logger.LogWarning(ex,
                "Could not remove {Identity} from SFU room {Room}; the roster has been updated anyway",
                identity, room);
        }
    }

    public async Task EndAsync(VoiceRoomKey key, CancellationToken ct = default)
    {
        if (!IsConfigured) return;

        var room = RoomName(key);
        var node = await registry.FindAsync(room, ct);
        if (node is null) return;

        try
        {
            await client.DeleteRoomAsync(node, room, ct);
        }
        catch (LiveKitControlException ex)
        {
            logger.LogWarning(ex, "Could not delete SFU room {Room}", room);
        }
        finally
        {
            // Dropped whether or not the delete landed.
            await registry.ForgetAsync(room, ct);
        }
    }

    public async Task<IReadOnlyList<VoiceSfuParticipant>> ListParticipantsAsync(
        VoiceRoomKey key, CancellationToken ct = default)
    {
        Require();

        var room = RoomName(key);
        var node = await registry.FindAsync(room, ct);
        if (node is null) return [];

        var participants = await Guard(
            "ListParticipants", () => client.ListParticipantsAsync(node, room, ct));

        return participants
            // A participant LiveKit has already marked disconnected is on its way out and is not
            // evidence of anything; treating one as present would have the roster repair re-add
            // somebody who is leaving.
            .Where(p => !string.Equals(p.State, "DISCONNECTED", StringComparison.OrdinalIgnoreCase))
            .Select(p => new VoiceSfuParticipant(
                p.Identity,
                LiveKitIdentity.UserOf(p.Identity),
                p.Tracks.Any(t => IsMicrophone(t)),
                p.Tracks.Select(t => t.Name ?? t.Sid).ToList()))
            .ToList();
    }

    /// <summary>Which region a room belongs to.</summary>
    private static string RegionFor(VoiceRoomKey key) => LiveKitRegions.Default;

    /// <summary>Our rights, in LiveKit's vocabulary.</summary>
    private static LiveKitGrants GrantsFor(VoiceMediaRights rights) => new(
        CanPublish: rights.MayPublishAudio || rights.MayPublishVideo,
        CanSubscribe: rights.MaySubscribe,
        // Data is how clients exchange the small out-of-band signals the hub does not carry.
        CanPublishData: rights.MayPublishAudio || rights.MayPublishVideo,
        CanPublishSources: rights.MayPublishVideo
            ? LiveKitSources.All
            : rights.MayPublishAudio
                ? LiveKitSources.AudioOnly
                : []);

    private static bool IsMicrophone(LiveKitTrack track) =>
        string.Equals(track.Source, LiveKitSources.Microphone, StringComparison.OrdinalIgnoreCase)
        || (track.Name is { } name && TrackNaming.IsMicrophone(name));

    private void Require()
    {
        if (!IsConfigured)
            throw new VoiceMediaException(
                "connect", VoiceMediaFailure.NotConfigured,
                "No LiveKit fleet is configured on this instance.");
    }

    private static async Task<T> Guard<T>(string operation, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (LiveKitControlException ex)
        {
            throw Translate(operation, ex);
        }
    }

    private static async Task Guard(string operation, Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (LiveKitControlException ex)
        {
            throw Translate(operation, ex);
        }
    }

    /// <summary>LiveKit's failure vocabulary, mapped onto ours.</summary>
    private static VoiceMediaException Translate(string operation, LiveKitControlException ex) =>
        new(operation,
            ex.IsTransient ? VoiceMediaFailure.Unavailable : VoiceMediaFailure.Rejected,
            ex.ResponseBody,
            ex);
}
