using Echo.Entitlements.Wire;
using Echo.Voice.Tracks;

namespace Echo.Voice.Rooms;

/// <summary>How a room is distributing right now.</summary>
public static class VoiceSubscriptionMode
{
    /// <summary>Everyone pulls everyone.</summary>
    public const string All = "all";

    /// <summary>Each subscriber pulls the ranked speakers plus their own pins.</summary>
    public const string ActiveSpeaker = "activeSpeaker";
}

/// <summary>Which simulcast layer of a video track a subscriber should pull.</summary>
public enum VoiceVideoLayer
{
    Low,
    Medium,
    High,
}

/// <summary>Wire names for <see cref="VoiceVideoLayer"/>.</summary>
public static class VoiceVideoLayers
{
    public const string High = "a";
    public const string Medium = "b";
    public const string Low = "c";

    public static string Name(VoiceVideoLayer layer) => layer switch
    {
        VoiceVideoLayer.Low => Low,
        VoiceVideoLayer.Medium => Medium,
        VoiceVideoLayer.High => High,
        _ => High,
    };

    public static VoiceVideoLayer? Parse(string? name) => name switch
    {
        Low => VoiceVideoLayer.Low,
        Medium => VoiceVideoLayer.Medium,
        High => VoiceVideoLayer.High,
        _ => null,
    };

    /// <summary>The cheaper of two choices.</summary>
    public static VoiceVideoLayer? Lower(VoiceVideoLayer? left, VoiceVideoLayer? right) =>
        left is null ? right
        : right is null ? left
        : (VoiceVideoLayer)Math.Min((int)left.Value, (int)right.Value);

    /// <summary>
    /// The best layer of a publisher's video that a granted rung permits to leave the room, or null
    /// when the rung does not bind what they said they would send.
    /// </summary>
    /// <param name="grantedRung">A rung of <c>video_quality</c>.</param>
    /// <param name="declaredHeight">The height the publisher said it intends to send.</param>
    public static VoiceVideoLayer? CeilingFor(string? grantedRung, int declaredHeight)
    {
        if (declaredHeight <= 0 || grantedRung is null) return null;
        if (!VideoRungs.TryMetrics(grantedRung, out var maxHeight, out _)) return null;

        // The 'none' rung, which the publish path refuses outright.
        if (maxHeight <= 0) return VoiceVideoLayer.Low;

        if (declaredHeight <= maxHeight) return null;
        if (declaredHeight <= maxHeight * 2) return VoiceVideoLayer.Medium;

        return VoiceVideoLayer.Low;
    }
}

/// <summary>
/// One track one subscriber should be pulling, with everything needed to pull it.
/// </summary>
/// <param name="MediaSessionId">
/// Null only for a share recorded before <see cref="ActiveScreenShare.MediaSessionId"/> existed,
/// where the handle lives in the <c>TrackPublished</c> event the client already has.
/// </param>
/// <param name="Layer">Null for audio, which is not simulcast.</param>
public sealed record VoiceSubscription(
    string UserId,
    string? MediaSessionId,
    string TrackName,
    string Kind,
    string? ShareId,
    string? Layer)
{
    /// <summary>
    /// What identifies this track when a subscribe request has to be matched against the plan.
    /// </summary>
    public static string MatchKey(string? mediaSessionId, string trackName) =>
        TrackNaming.IsScreenShare(trackName) ? trackName : $"{mediaSessionId}/{trackName}";

    public string MatchKey() => MatchKey(MediaSessionId, TrackName);
}

/// <summary>One subscriber's whole subscription set.</summary>
public sealed record VoiceSubscriptionSet(string UserId, IReadOnlyList<VoiceSubscription> Tracks)
{
    /// <summary>
    /// A stable identity for this exact set, used to decide whether anything needs announcing.
    /// </summary>
    public string Signature { get; } = string.Join(
        '|',
        Tracks
            .Select(t => $"{t.UserId}/{t.MediaSessionId}/{t.TrackName}/{t.Layer ?? "-"}")
            .OrderBy(s => s, StringComparer.Ordinal));

    public static VoiceSubscriptionSet Empty(string userId) => new(userId, []);
}

/// <summary>What the server has decided every subscriber in one room should be pulling.</summary>
/// <param name="Restricted">
/// Whether anything was withheld from anybody for a reason other than ranking - a collapsed tile, a
/// backgrounded client, a share's audio nobody asked for, a publisher past the video cap.
/// </param>
public sealed record VoiceSubscriptionPlan(
    string Mode,
    long Revision,
    IReadOnlyList<string> ActiveSpeakers,
    IReadOnlyList<string> VideoPublishers,
    IReadOnlyDictionary<string, VoiceSubscriptionSet> Sets,
    bool Restricted = false)
{
    /// <summary>Whether this plan asks for less than all-to-all would.</summary>
    public bool IsSelective => Mode == VoiceSubscriptionMode.ActiveSpeaker || Restricted;

    public VoiceSubscriptionSet For(string userId) =>
        Sets.TryGetValue(userId, out var set) ? set : VoiceSubscriptionSet.Empty(userId);

    /// <summary>Total tracks being distributed across the room - the fan-out this plan asks the SFU
    /// for, and the number the cost model is expressed in.</summary>
    public long TotalSubscriptions => Sets.Values.Sum(s => (long)s.Tracks.Count);

    /// <summary>A room nobody has planned for: no sets, no speakers, all-to-all.</summary>
    public static VoiceSubscriptionPlan Unplanned { get; } = new(
        VoiceSubscriptionMode.All, 0, [], [],
        new Dictionary<string, VoiceSubscriptionSet>(StringComparer.Ordinal));
}
