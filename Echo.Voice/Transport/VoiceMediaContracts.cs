using Echo.Voice.Rooms;

namespace Echo.Voice.Transport;

/// <summary>What one participant is allowed to do with media in a room.</summary>
public sealed record VoiceMediaRights(
    bool MayPublishAudio, bool MayPublishVideo, bool MaySubscribe)
{
    /// <summary>Speaks and sees everything. The ordinary participant.</summary>
    public static readonly VoiceMediaRights Full = new(true, true, true);

    /// <summary>In the room, audible, and unable to turn a camera on however the client is patched.
    /// What a joiner past the room's video budget holds - a state, not a refusal.</summary>
    public static readonly VoiceMediaRights AudioOnly = new(true, false, true);

    /// <summary>Hears the room and sends nothing. What a member without Speak holds.</summary>
    public static readonly VoiceMediaRights Listener = new(false, false, true);
}

/// <summary>Everything a client needs to open its own connection to the SFU.</summary>
/// <param name="Url">The node's public signalling URL.</param>
/// <param name="Token">
/// A signed, short-lived capability naming exactly this room, this participant and these rights.
/// </param>
/// <param name="Identity">The participant key at the SFU.</param>
public sealed record VoiceConnection(
    string Backend,
    string Url,
    string Token,
    string Room,
    string Identity,
    DateTimeOffset ExpiresAt);

/// <summary>One track as the SFU currently sees it.</summary>
/// <param name="Height">
/// The tallest encoding this publication can produce, measured by the SFU rather than declared by
/// the client.
/// </param>
public sealed record VoiceSfuTrack(string Name, string Sid, bool IsVideo, int Height);

/// <summary>One participant as the SFU currently sees them.</summary>
public sealed record VoiceSfuParticipant(
    string Identity,
    string UserId,
    bool IsPublishing,
    IReadOnlyList<string> TrackNames,
    IReadOnlyList<VoiceSfuTrack>? Tracks = null)
{
    public IReadOnlyList<VoiceSfuTrack> Media => Tracks ?? [];
}

/// <summary>Why a control-plane operation failed, in terms a caller can map to a status code without
/// knowing which SFU produced it.</summary>
public enum VoiceMediaFailure
{
    /// <summary>No SFU is configured on this instance.</summary>
    NotConfigured,

    /// <summary>The SFU refused the request: a malformed room name, an unknown participant, a bad
    /// credential. Retrying the same thing will not help.</summary>
    Rejected,

    /// <summary>The control plane could not be reached, or answered a 5xx.</summary>
    Unavailable,
}

/// <summary>The SFU rejected an operation, or could not be asked.</summary>
public sealed class VoiceMediaException(
    string operation, VoiceMediaFailure failure, string detail, Exception? inner = null)
    : Exception($"Voice SFU '{operation}' failed ({failure}): {detail}", inner)
{
    public string Operation { get; } = operation;
    public VoiceMediaFailure Failure { get; } = failure;
    public string Detail { get; } = detail;
}

/// <summary>
/// Everything the voice rooms need from an SFU, in vocabulary that is not any particular SFU's.
/// </summary>
public interface IVoiceSfu
{
    /// <summary>Names the backend in the connection payload so a client can pick its SDK without
    /// inferring it from the shape of the response.</summary>
    string Backend { get; }

    /// <summary>Whether this instance has an SFU at all.</summary>
    bool IsConfigured { get; }

    /// <summary>Ensures the room exists and mints this participant's way into it.</summary>
    /// <param name="maxParticipants">The room's hard ceiling at the SFU, or null for none.</param>
    Task<VoiceConnection> ConnectAsync(
        VoiceRoomKey key,
        string identity,
        string? displayName,
        VoiceMediaRights rights,
        int? maxParticipants = null,
        CancellationToken ct = default);

    /// <summary>Changes what a participant already in the room may do.</summary>
    Task<bool> UpdateRightsAsync(
        VoiceRoomKey key, string identity, VoiceMediaRights rights, CancellationToken ct = default);

    /// <summary>Removes one participant from the room at the SFU.</summary>
    Task DisconnectAsync(VoiceRoomKey key, string identity, CancellationToken ct = default);

    /// <summary>Ends the room outright.</summary>
    Task EndAsync(VoiceRoomKey key, CancellationToken ct = default);

    /// <summary>Who the SFU currently has in the room.</summary>
    Task<IReadOnlyList<VoiceSfuParticipant>> ListParticipantsAsync(
        VoiceRoomKey key, CancellationToken ct = default);
}
