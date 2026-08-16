using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Echo.Realtime.LiveKit;

/// <summary>Which media sources a participant may publish.</summary>
public static class LiveKitSources
{
    public const string Microphone = "microphone";
    public const string Camera = "camera";
    public const string ScreenShare = "screen_share";
    public const string ScreenShareAudio = "screen_share_audio";

    /// <summary>Everything a voice participant can send.</summary>
    public static readonly IReadOnlyList<string> All =
        [Microphone, Camera, ScreenShare, ScreenShareAudio];

    /// <summary>Microphone only, which is what a participant reduced to audio-only holds.</summary>
    public static readonly IReadOnlyList<string> AudioOnly = [Microphone];
}

/// <summary>What one token is allowed to do.</summary>
/// <param name="CanPublishSources">
/// Null means "whatever <paramref name="CanPublish"/> says", which is LiveKit's own behaviour.
/// </param>
/// <param name="Hidden">Present in the room but invisible to other participants.</param>
public sealed record LiveKitGrants(
    bool CanPublish,
    bool CanSubscribe,
    bool CanPublishData = true,
    IReadOnlyList<string>? CanPublishSources = null,
    bool Hidden = false)
{
    /// <summary>A full participant: sends everything, receives everything.</summary>
    public static readonly LiveKitGrants Participant = new(CanPublish: true, CanSubscribe: true);

    /// <summary>In the room and audible, but nothing above a microphone leaves them.</summary>
    public static readonly LiveKitGrants AudioOnly = new(
        CanPublish: true, CanSubscribe: true, CanPublishSources: LiveKitSources.AudioOnly);

    /// <summary>Receives only.</summary>
    public static readonly LiveKitGrants Listener = new(
        CanPublish: false, CanSubscribe: true, CanPublishData: false);
}

/// <summary>Mints the JWTs that are the entire authorization story for the SFU.</summary>
public static class LiveKitToken
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A join token for one participant of one room.</summary>
    /// <param name="identity">The participant key, and it has to be stable and unique.</param>
    public static string ForJoin(
        LiveKitOptions options,
        string room,
        string identity,
        string? displayName = null,
        LiveKitGrants? grants = null,
        TimeSpan? ttl = null)
    {
        var effective = grants ?? LiveKitGrants.Participant;

        var video = new Dictionary<string, object>
        {
            ["room"] = room,
            ["roomJoin"] = true,
            // Written out one by one, never conditionally. An omitted flag is a grant of everything.
            ["canPublish"] = effective.CanPublish,
            ["canSubscribe"] = effective.CanSubscribe,
            ["canPublishData"] = effective.CanPublishData,
            ["hidden"] = effective.Hidden,
        };

        // Absent rather than empty when nothing narrows it: an empty array is a claim that the
        // participant may publish no source at all, which is a different thing from "canPublish
        // decides".
        if (effective.CanPublishSources is { Count: > 0 } sources)
            video["canPublishSources"] = sources;

        return Build(options, identity, displayName, video, ttl ?? options.JoinTokenTtl);
    }

    /// <summary>An admin token for one control-plane call.</summary>
    /// <param name="room">Scopes <c>roomAdmin</c> to one room.</param>
    public static string ForAdmin(
        LiveKitOptions options, string? room = null, TimeSpan? ttl = null)
    {
        var video = new Dictionary<string, object>
        {
            ["roomCreate"] = true,
            ["roomList"] = true,
            ["roomAdmin"] = true,
        };
        if (room is not null) video["room"] = room;

        return Build(options, "echo-backend", null, video, ttl ?? options.AdminTokenTtl);
    }

    private static string Build(
        LiveKitOptions options,
        string identity,
        string? name,
        Dictionary<string, object> video,
        TimeSpan ttl)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey) || string.IsNullOrWhiteSpace(options.ApiSecret))
            throw new InvalidOperationException(
                "LIVEKIT_API_KEY / LIVEKIT_API_SECRET are not configured - no token can be minted.");

        var now = DateTimeOffset.UtcNow;

        var payload = new Dictionary<string, object>
        {
            ["iss"] = options.ApiKey,
            ["sub"] = identity,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.Add(ttl).ToUnixTimeSeconds(),
            ["identity"] = identity,
            ["video"] = video,
        };

        // Added conditionally rather than through JsonIgnoreCondition.WhenWritingNull, which applies
        // to object properties and not to dictionary values - a null display name would otherwise
        // serialise as "name": null and be shown to the room as an empty label.
        if (!string.IsNullOrEmpty(name)) payload["name"] = name;

        var header = new Dictionary<string, string>
        {
            ["alg"] = "HS256",
            ["typ"] = "JWT",
        };

        var signingInput =
            $"{B64Url(JsonSerializer.SerializeToUtf8Bytes(header, Json))}." +
            $"{B64Url(JsonSerializer.SerializeToUtf8Bytes(payload, Json))}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(options.ApiSecret));
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));

        return $"{signingInput}.{B64Url(signature)}";
    }

    private static string B64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

/// <summary>Turns a user into a LiveKit participant identity, and back.</summary>
public static class LiveKitIdentity
{
    /// <summary>Separates the user id from the secondary-connection tag.</summary>
    public const char Separator = '#';

    /// <summary>The identity carrying a user's microphone. One per user per room, by design.</summary>
    public static string Primary(string userId) => userId;

    /// <summary>A secondary connection belonging to the same user, tagged so it cannot evict their
    /// primary one.</summary>
    public static string Secondary(string userId, string tag) =>
        $"{userId}{Separator}{Sanitise(tag)}";

    /// <summary>The user behind an identity, whichever kind it is.</summary>
    public static string UserOf(string identity)
    {
        var separator = identity.IndexOf(Separator);
        return separator < 0 ? identity : identity[..separator];
    }

    /// <summary>Whether this identity is the one carrying the user's microphone.</summary>
    public static bool IsPrimary(string identity) => identity.IndexOf(Separator) < 0;

    /// <summary>Keeps a caller-supplied tag from smuggling a separator in and re-pointing
    /// <see cref="UserOf"/> at somebody else.</summary>
    private static string Sanitise(string tag)
    {
        var clean = new string(tag.Where(char.IsLetterOrDigit).Take(32).ToArray());
        return clean.Length > 0 ? clean : "alt";
    }
}
