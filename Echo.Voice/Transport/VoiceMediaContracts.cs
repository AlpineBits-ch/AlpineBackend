namespace Echo.Voice.Transport;

/// <summary>An SDP offer or answer. Neutral spelling of what every WebRTC transport exchanges.</summary>
public record VoiceSessionDescription(string Type, string Sdp);

/// <summary>Which way a track flows, from the caller's point of view.</summary>
public static class VoiceTrackDirection
{
    /// <summary>The caller is sending this track.</summary>
    public const string Publish = "publish";

    /// <summary>The caller is pulling somebody else's track.</summary>
    public const string Subscribe = "subscribe";
}

/// <summary>One track in a negotiation.</summary>
/// <param name="Direction">See <see cref="VoiceTrackDirection"/>.</param>
/// <param name="Mid">Publish only: the transceiver MID after <c>setLocalDescription</c>.</param>
/// <param name="TrackName">Publish: the name to publish under.</param>
/// <param name="MediaSessionId">Subscribe only: the media session of the peer being pulled.</param>
public record VoiceTrackRef(
    string Direction,
    string? Mid = null,
    string? TrackName = null,
    string? MediaSessionId = null);

public record VoiceNegotiateRequest(
    VoiceSessionDescription SessionDescription,
    IReadOnlyList<VoiceTrackRef> Tracks);

/// <param name="Mid">Absent when the transport could not set the track up.</param>
public record VoiceTrackResult(
    string? Mid,
    string TrackName,
    string? MediaSessionId,
    string? Direction);

public record VoiceNegotiateResponse(
    VoiceSessionDescription SessionDescription,
    IReadOnlyList<VoiceTrackResult> Tracks,
    bool RequiresImmediateRenegotiation);

/// <summary>Why a media operation failed, in terms a caller can map to a status code without
/// knowing which SFU produced it.</summary>
public enum VoiceMediaFailure
{
    /// <summary>The transport rejected the request: a malformed offer, an unknown session, a bad
    /// token. Retrying the same thing will not help.</summary>
    Rejected,

    /// <summary>A track being subscribed to does not exist on the publisher's session.</summary>
    TrackNotFound,

    /// <summary>The transport itself is having a bad moment (5xx, timeout).</summary>
    Unavailable,
}

/// <summary>
/// The media transport rejected an operation, or accepted it in a way the caller cannot use.
/// </summary>
public sealed class VoiceMediaException(
    string operation, VoiceMediaFailure failure, string detail, Exception? inner = null)
    : Exception($"Voice media transport '{operation}' failed ({failure}): {detail}", inner)
{
    public string Operation { get; } = operation;
    public VoiceMediaFailure Failure { get; } = failure;
    public string Detail { get; } = detail;
}

/// <summary>
/// Everything the voice rooms need from an SFU, in vocabulary that is not any particular SFU's.
/// </summary>
public interface IVoiceMediaTransport
{
    /// <summary>Names the backend in the session handshake so a client can branch on it without
    /// guessing. Today always <c>cloudflare</c>.</summary>
    string Backend { get; }

    Task<string> CreateSessionAsync(CancellationToken ct = default);

    /// <summary>Publishes tracks.</summary>
    Task<VoiceNegotiateResponse> PublishAsync(
        string mediaSessionId, VoiceNegotiateRequest request, CancellationToken ct = default);

    /// <summary>Pulls tracks, retried across the window in which a publisher exists but is not yet
    /// sending packets.</summary>
    Task<VoiceNegotiateResponse> SubscribeAsync(
        string mediaSessionId, VoiceNegotiateRequest request, CancellationToken ct = default);

    Task<VoiceSessionDescription> RenegotiateAsync(
        string mediaSessionId, VoiceSessionDescription offer, CancellationToken ct = default);

    Task CloseTracksAsync(
        string mediaSessionId, IEnumerable<string> trackNames, CancellationToken ct = default);
}
