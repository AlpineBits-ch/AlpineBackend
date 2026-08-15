using FirebaseAdmin.Messaging;
using Guild.Contracts;

namespace Messaging.Application.Services;

/// <summary>One endpoint a voice-ring notification goes to.</summary>
public readonly record struct VoiceRingPushRecipient(string Token, string UserId, bool Localized);

/// <summary>The FCM send for "somebody wants you in a voice channel".</summary>
public static class VoiceRingPushService
{
    /// <summary>Title substituted for a recipient with HidePushContent (privacy spec T2-23).</summary>
    public const string HiddenContentTitle = "Voice invite";

    /// <summary>Body for the same.</summary>
    public const string HiddenContentBody = "Someone asked you to join a voice channel";

    /// <summary>The Android notification channel these land on.</summary>
    public const string AndroidChannelId = "voice_invites";

    public static async Task SendAsync(
        IEnumerable<VoiceRingPushRecipient> recipients,
        VoiceRingPushPayload payload,
        ILogger? logger = null)
    {
        foreach (var recipient in recipients)
        {
            try
            {
                await FirebaseMessaging.DefaultInstance.SendAsync(Build(payload, recipient));
            }
            catch (Exception e)
            {
                // One dead token must not cost the other handsets their invitation.
                logger?.LogWarning(e, "Voice ring push failed for ring {RingId}", payload.RingId);
            }
        }
    }

    /// <summary>The whole FCM message for one recipient.</summary>
    public static Message Build(VoiceRingPushPayload payload, VoiceRingPushRecipient recipient)
    {
        var data = BuildData(payload, recipient);

        // The cancel is data-only on both platforms.
        if (payload.Cancel)
        {
            return new Message
            {
                Token = recipient.Token,
                Data = data,
                Android = new AndroidConfig { Priority = Priority.High },
                Apns = new ApnsConfig
                {
                    Headers = new Dictionary<string, string>
                    {
                        ["apns-priority"] = "5",
                        ["apns-push-type"] = "background",
                        ["apns-collapse-id"] = CollapseKey(payload),
                    },
                    Aps = new Aps { ContentAvailable = true },
                },
            };
        }

        var title = payload.Hidden ? HiddenContentTitle : payload.Title;

        var body = payload.Hidden ? HiddenContentBody : Truncate(payload.Body, MaxBodyChars);

        var bodyKey = ResolveKey(payload, recipient);
        var bodyArgs = bodyKey is null || payload.Hidden || payload.BodyLocArgs.Count == 0
            ? null
            : payload.BodyLocArgs.ToArray();

        return new Message
        {
            Token = recipient.Token,
            Data = data,
            Android = new AndroidConfig
            {
                // An invitation held until the handset leaves Doze arrives after everyone has gone
                // to bed, which for "we are in here now" is worthless.
                Priority = Priority.High,
                Notification = new AndroidNotification
                {
                    ChannelId = AndroidChannelId,
                    // Title and body still travel alongside the key.
                    Title = title,
                    Body = body,
                    BodyLocKey = bodyKey,
                    BodyLocArgs = bodyArgs,
                    // Replaces this ring's earlier notification rather than stacking a second one.
                    Tag = CollapseKey(payload),
                },
            },
            Apns = new ApnsConfig
            {
                Headers = new Dictionary<string, string>
                {
                    ["apns-priority"] = "10",
                    ["apns-push-type"] = "alert",
                    ["apns-collapse-id"] = CollapseKey(payload),
                },
                Aps = new Aps
                {
                    Alert = new ApsAlert
                    {
                        Title = title,
                        Body = body,
                        LocKey = bodyKey,
                        LocArgs = bodyArgs,
                    },
                    Sound = "default",
                },
            },
        };
    }

    /// <summary>The FCM data payload, and the contract the clients parse.</summary>
    public static Dictionary<string, string> BuildData(
        VoiceRingPushPayload payload, VoiceRingPushRecipient recipient)
    {
        var data = new Dictionary<string, string>
        {
            ["type"] = "voice_ring",
            ["ringId"] = payload.RingId,
            ["guildId"] = payload.GuildId,
            ["channelId"] = payload.ChannelId,
            ["inviterId"] = payload.InviterId,
            ["recipientUserId"] = recipient.UserId,
        };

        if (payload.Cancel)
        {
            data["ringSubtype"] = "cancel";
            data["cancelReason"] = payload.CancelReason ?? "";
            // The device that resolved the ring is addressed too and drops this itself by matching
            // its own id.
            data["excludeDeviceId"] = payload.ExcludeDeviceId ?? "";
            return data;
        }

        data["ringSubtype"] = "invite";
        data["hidden"] = payload.Hidden ? "1" : "0";
        data["expiresInSeconds"] = payload.ExpiresInSeconds.ToString();
        data["title"] = payload.Hidden ? HiddenContentTitle : payload.Title;
        data["body"] = payload.Hidden ? HiddenContentBody : Truncate(payload.Body, MaxBodyChars);

        if (!string.IsNullOrWhiteSpace(payload.InviterAvatarUrl) && !payload.Hidden)
            data["inviterAvatarUrl"] = payload.InviterAvatarUrl!;

        var bodyKey = ResolveKey(payload, recipient);
        if (bodyKey is not null)
        {
            data["bodyLocKey"] = bodyKey;
            // FCM data values are strings only, so the argument list travels as a JSON array.
            if (!payload.Hidden && payload.BodyLocArgs.Count > 0)
                data["bodyLocArgs"] = System.Text.Json.JsonSerializer.Serialize(payload.BodyLocArgs);
        }

        return data;
    }

    /// <summary>Which localization key, if any, this recipient may be sent.</summary>
    private static string? ResolveKey(VoiceRingPushPayload payload, VoiceRingPushRecipient recipient)
    {
        if (!recipient.Localized) return null;
        return payload.Hidden ? VoiceLocKeys.HiddenBody : payload.BodyLocKey;
    }

    private const int MaxBodyChars = 200;

    /// <summary>What makes two notifications "the same ring, again".</summary>
    private static string CollapseKey(VoiceRingPushPayload payload) =>
        Truncate($"voiceRing:{payload.RingId}", 64);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}

public class VoiceRingPushPayload
{
    public required string RingId { get; init; }
    public required string GuildId { get; init; }
    public required string ChannelId { get; init; }
    public required string InviterId { get; init; }
    public string? InviterAvatarUrl { get; init; }
    public int ExpiresInSeconds { get; init; }

    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string? BodyLocKey { get; init; }
    public IReadOnlyList<string> BodyLocArgs { get; init; } = [];

    /// <summary>Whether this recipient has HidePushContent set.</summary>
    public bool Hidden { get; init; }

    public bool Cancel { get; init; }
    public string? CancelReason { get; init; }
    public string? ExcludeDeviceId { get; init; }
}
