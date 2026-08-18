using FirebaseAdmin.Messaging;

namespace Messaging.Application.Services;

/// <summary>One endpoint a scene notification goes to.</summary>
public readonly record struct ScenePushRecipient(string Token, string UserId);

/// <summary>The FCM send for "a scene is waiting on a turn".</summary>
public static class ScenePushService
{
    /// <summary>
    /// What the notification renders under when the character has no resolvable name. The account
    /// behind the character is never substituted: the whole point of a persona push is that a lock
    /// screen does not say who plays whom.
    /// </summary>
    public const string MaskedPersonaName = "A character";

    /// <summary>Title substituted for a recipient with HidePushContent (privacy spec T2-23).</summary>
    public const string HiddenContentTitle = "Scene";

    public const string HiddenContentBody = "A scene needs your attention";

    /// <summary>The notification channel the Android client creates for these.</summary>
    public const string AndroidChannelId = "scenes";

    public static async Task SendAsync(
        IEnumerable<ScenePushRecipient> recipients, ScenePushPayload payload, ILogger? logger = null)
    {
        foreach (var recipient in recipients)
        {
            try
            {
                await FirebaseMessaging.DefaultInstance.SendAsync(Build(payload, recipient));
            }
            catch (Exception e)
            {
                // One dead token must not cost the other recipients their notification.
                logger?.LogWarning(e, "Scene push failed for {ChannelId}", payload.ChannelId);
            }
        }
    }

    /// <summary>The whole FCM message for one recipient.</summary>
    public static Message Build(ScenePushPayload payload, ScenePushRecipient recipient)
    {
        var hidden = payload.HideContentForUserIds.Contains(recipient.UserId);
        var title = hidden ? HiddenContentTitle : Title(payload);
        var body = hidden ? HiddenContentBody : Body(payload);

        return new Message
        {
            Token = recipient.Token,
            Data = BuildData(payload, recipient.UserId),
            Android = new AndroidConfig
            {
                Priority = Priority.High,
                Notification = new AndroidNotification
                {
                    ChannelId = AndroidChannelId,
                    Title = title,
                    Body = body,
                    // The second reminder about one scene replaces the first rather than stacking.
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
                    Alert = new ApsAlert { Title = title, Body = body },
                    Sound = "default",
                },
            },
        };
    }

    /// <summary>The FCM data payload one recipient receives.</summary>
    public static Dictionary<string, string> BuildData(ScenePushPayload payload, string userId)
    {
        var hidden = payload.HideContentForUserIds.Contains(userId);

        var data = new Dictionary<string, string>
        {
            ["type"] = "scene",
            ["kind"] = payload.Escalated ? "scene.turnStalled" : "scene.turn",
            ["guildId"] = payload.GuildId,
            ["channelId"] = payload.ChannelId,
            ["recipientUserId"] = userId,
            ["hidden"] = hidden ? "1" : "0",
            ["title"] = hidden ? HiddenContentTitle : Title(payload),
            ["body"] = hidden ? HiddenContentBody : Body(payload),
        };

        if (hidden) return data;

        // The character travels even when its name does not: an iOS notification-service extension
        // has no way to look a persona up, and personaHidden is what tells it to mask rather than
        // to fall back to the sender's account.
        data["personaId"] = payload.PersonaId;
        data["personaHidden"] = payload.PersonaHidden ? "1" : "0";
        data["authorDisplayName"] = Title(payload);

        if (!string.IsNullOrWhiteSpace(payload.SceneName)) data["sceneName"] = payload.SceneName;
        if (payload.DeadlineAt is { } deadline) data["deadlineAt"] = deadline.ToString("O");

        return data;
    }

    private static string Title(ScenePushPayload payload) =>
        payload.PersonaHidden || string.IsNullOrWhiteSpace(payload.PersonaName)
            ? MaskedPersonaName
            : payload.PersonaName;

    private static string Body(ScenePushPayload payload)
    {
        var where = string.IsNullOrWhiteSpace(payload.SceneName) ? "a scene" : payload.SceneName;

        return payload.Escalated
            ? $"This turn has gone quiet in {where}"
            : $"It is your turn in {where}";
    }

    private static string CollapseKey(ScenePushPayload payload) => $"scene:{payload.ChannelId}";
}

/// <summary>Everything one scene notification renders from.</summary>
public class ScenePushPayload
{
    public required string GuildId { get; init; }
    public required string ChannelId { get; init; }
    public required string PersonaId { get; init; }

    /// <summary>The character's name in its guild, empty when it could not be resolved.</summary>
    public string PersonaName { get; init; } = "";

    /// <summary>Set when the name is withheld, so the client masks rather than guessing.</summary>
    public bool PersonaHidden { get; init; }

    public string SceneName { get; init; } = "";
    public DateTimeOffset? DeadlineAt { get; init; }

    /// <summary>Addressed to the GM after a second miss rather than to the player.</summary>
    public bool Escalated { get; init; }

    /// <summary>Recipients who have HidePushContent set.</summary>
    public IReadOnlySet<string> HideContentForUserIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}
