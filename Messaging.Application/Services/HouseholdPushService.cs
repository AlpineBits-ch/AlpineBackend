using FirebaseAdmin.Messaging;

namespace Messaging.Application.Services;

/// <summary>The FCM send for a household notification.</summary>
public static class HouseholdPushService
{
    /// <summary>Body substituted for a recipient with HidePushContent (privacy spec T2-23). The
    /// title still names the module, because "Household" leaks nothing beyond the fact that the
    /// person is in a household guild - which their own home screen already shows.</summary>
    public const string HiddenContentBody = "Something needs your attention at home";

    public const string HiddenContentTitle = "Home";

    private const int MaxBodyChars = 300;

    public static async Task SendAsync(
        IEnumerable<(string Token, string UserId)> recipients,
        HouseholdPushPayload payload,
        ILogger? logger = null)
    {
        foreach (var (token, userId) in recipients)
        {
            var hidden = payload.HideContentForUserIds.Contains(userId);
            var title = hidden ? HiddenContentTitle : payload.Title;
            var body = hidden ? HiddenContentBody : Truncate(payload.Body, MaxBodyChars);

            try
            {
                await FirebaseMessaging.DefaultInstance.SendAsync(new Message
                {
                    Token = token,
                    Data = BuildData(payload, userId),
                    Android = new AndroidConfig
                    {
                        // A chore reminder held until the device leaves Doze arrives the next
                        // morning, which for "the bins go out tonight" is worthless.
                        Priority = Priority.High,
                        Notification = new AndroidNotification
                        {
                            Title = title,
                            Body = body,
                            // Collapses an earlier notification about the same row rather than
                            // stacking two of them - the second reminder for one chore replaces
                            // the first.
                            Tag = payload.TargetId,
                        },
                    },
                    Apns = new ApnsConfig
                    {
                        Headers = new Dictionary<string, string>
                        {
                            ["apns-priority"] = "10",
                            ["apns-push-type"] = "alert",
                            ["apns-collapse-id"] = payload.TargetId ?? payload.Kind,
                        },
                        Aps = new Aps
                        {
                            Alert = new ApsAlert { Title = title, Body = body },
                            Sound = "default",
                        },
                    },
                });
            }
            catch (Exception e)
            {
                // One dead token must not cost the other recipients their notification.
                logger?.LogWarning(e, "Household push failed for {Kind} {TargetId}", payload.Kind, payload.TargetId);
            }
        }
    }

    /// <summary>The FCM data payload one recipient receives.</summary>
    public static Dictionary<string, string> BuildData(HouseholdPushPayload payload, string recipientUserId)
    {
        var hidden = payload.HideContentForUserIds.Contains(recipientUserId);

        var data = new Dictionary<string, string>
        {
            ["type"] = "household",
            ["kind"] = payload.Kind,
            ["guildId"] = payload.GuildId,
            ["recipientUserId"] = recipientUserId,
            ["hidden"] = hidden ? "1" : "0",
            // Title and body travel in data as well as in the alert so a foreground client can
            // render them without re-fetching, exactly as the message path does.
            ["title"] = hidden ? HiddenContentTitle : payload.Title,
            ["body"] = hidden ? HiddenContentBody : Truncate(payload.Body, MaxBodyChars),
        };

        if (!string.IsNullOrWhiteSpace(payload.ChannelId)) data["channelId"] = payload.ChannelId!;
        if (!string.IsNullOrWhiteSpace(payload.TargetId)) data["targetId"] = payload.TargetId!;

        return data;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}

public class HouseholdPushPayload
{
    public required string GuildId { get; init; }
    public string? ChannelId { get; init; }
    public required string Kind { get; init; }
    public string? TargetId { get; init; }
    public required string Title { get; init; }
    public string Body { get; init; } = "";

    /// <summary>Recipients who have HidePushContent set.</summary>
    public IReadOnlySet<string> HideContentForUserIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}
