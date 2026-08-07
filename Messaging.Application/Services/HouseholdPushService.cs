using FirebaseAdmin.Messaging;
using Guild.Contracts;

namespace Messaging.Application.Services;

/// <summary>
/// One recipient of a household push: where to send it, who it is for, and what their build can
/// read.
/// </summary>
public readonly record struct HouseholdPushRecipient(string Token, string UserId, bool Localized);

/// <summary>The FCM send for a household notification.</summary>
public static class HouseholdPushService
{
    /// <summary>Body substituted for a recipient with HidePushContent (privacy spec T2-23). The
    /// title still names the module, because "Household" leaks nothing beyond the fact that the
    /// person is in a household guild - which their own home screen already shows.</summary>
    public const string HiddenContentBody = "Something needs your attention at home";

    public const string HiddenContentTitle = "Home";

    /// <summary>
    /// The notification channel the Android client creates for these (see
    /// <c>HouseholdNotifier.channel</c> in the app).
    /// </summary>
    public const string AndroidChannelId = "household";

    private const int MaxBodyChars = 300;

    public static async Task SendAsync(
        IEnumerable<HouseholdPushRecipient> recipients,
        HouseholdPushPayload payload,
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
                // One dead token must not cost the other recipients their notification.
                logger?.LogWarning(e, "Household push failed for {Kind} {TargetId}", payload.Kind, payload.TargetId);
            }
        }
    }

    /// <summary>The whole FCM message for one recipient.</summary>
    public static Message Build(HouseholdPushPayload payload, HouseholdPushRecipient recipient)
    {
        var hidden = payload.HideContentForUserIds.Contains(recipient.UserId);

        var title = Resolve(
            hidden, recipient.Localized,
            HiddenContentTitle, HouseholdLocKeys.HiddenTitle,
            payload.Title, payload.TitleLocKey, payload.TitleLocArgs);

        var body = Resolve(
            hidden, recipient.Localized,
            HiddenContentBody, HouseholdLocKeys.HiddenBody,
            Truncate(payload.Body, MaxBodyChars), payload.BodyLocKey, payload.BodyLocArgs);

        return new Message
        {
            Token = recipient.Token,
            Data = BuildData(payload, recipient),
            Android = new AndroidConfig
            {
                // A chore reminder held until the device leaves Doze arrives the next morning,
                // which for "the bins go out tonight" is worthless.
                Priority = Priority.High,
                Notification = new AndroidNotification
                {
                    ChannelId = AndroidChannelId,
                    // Title and body are still sent alongside the keys.
                    Title = title.Text,
                    Body = body.Text,
                    TitleLocKey = title.Key,
                    TitleLocArgs = title.Args,
                    BodyLocKey = body.Key,
                    BodyLocArgs = body.Args,
                    // Collapses an earlier notification about the same row rather than stacking two
                    // of them - the second reminder for one chore replaces the first.
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
                    Alert = new ApsAlert
                    {
                        Title = title.Text,
                        Body = body.Text,
                        TitleLocKey = title.Key,
                        TitleLocArgs = title.Args,
                        LocKey = body.Key,
                        LocArgs = body.Args,
                    },
                    Sound = "default",
                },
            },
        };
    }

    /// <summary>One line of the notification, already reduced to what this recipient may be sent.
    /// <see cref="Key"/> is null whenever a key must not be used: the reader's build cannot resolve
    /// one, or the text is something the user typed and there is nothing to translate.</summary>
    private readonly record struct Line(string Text, string? Key, IReadOnlyList<string>? Args);

    /// <summary>
    /// Picks between the hidden stand-in and the real copy, then between the key and the English -
    /// in that order, because a hidden notification is hidden in every language.
    /// </summary>
    private static Line Resolve(
        bool hidden, bool localized,
        string hiddenText, string hiddenKey,
        string text, string? key, IReadOnlyList<string> args)
    {
        if (hidden) return new Line(hiddenText, localized ? hiddenKey : null, null);
        if (!localized || key is null) return new Line(text, null, null);

        return new Line(text, key, args.Count == 0 ? null : [.. args]);
    }

    /// <summary>The FCM data payload one recipient receives.</summary>
    public static Dictionary<string, string> BuildData(
        HouseholdPushPayload payload, HouseholdPushRecipient recipient)
    {
        var hidden = payload.HideContentForUserIds.Contains(recipient.UserId);

        var title = Resolve(
            hidden, recipient.Localized,
            HiddenContentTitle, HouseholdLocKeys.HiddenTitle,
            payload.Title, payload.TitleLocKey, payload.TitleLocArgs);

        var body = Resolve(
            hidden, recipient.Localized,
            HiddenContentBody, HouseholdLocKeys.HiddenBody,
            Truncate(payload.Body, MaxBodyChars), payload.BodyLocKey, payload.BodyLocArgs);

        var data = new Dictionary<string, string>
        {
            ["type"] = "household",
            ["kind"] = payload.Kind,
            ["guildId"] = payload.GuildId,
            ["recipientUserId"] = recipient.UserId,
            ["hidden"] = hidden ? "1" : "0",
            // Title and body travel in data as well as in the alert so a foreground client can
            // render them without re-fetching, exactly as the message path does.
            ["title"] = title.Text,
            ["body"] = body.Text,
        };

        if (!string.IsNullOrWhiteSpace(payload.ChannelId)) data["channelId"] = payload.ChannelId!;
        if (!string.IsNullOrWhiteSpace(payload.TargetId)) data["targetId"] = payload.TargetId!;

        // FCM data values are strings only, so the argument lists travel as JSON arrays.
        if (title.Key is not null)
        {
            data["titleLocKey"] = title.Key;
            if (title.Args is { Count: > 0 }) data["titleLocArgs"] = Encode(title.Args);
        }

        if (body.Key is not null)
        {
            data["bodyLocKey"] = body.Key;
            if (body.Args is { Count: > 0 }) data["bodyLocArgs"] = Encode(body.Args);
        }

        return data;
    }

    private static string Encode(IReadOnlyList<string> args) =>
        System.Text.Json.JsonSerializer.Serialize(args);

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

    /// <summary>The localization key <see cref="Title"/> is the English rendering of, or null when
    /// the title is user content. See <c>HouseholdPushRequested.TitleLocKey</c>.</summary>
    public string? TitleLocKey { get; init; }
    public IReadOnlyList<string> TitleLocArgs { get; init; } = [];

    public string? BodyLocKey { get; init; }
    public IReadOnlyList<string> BodyLocArgs { get; init; } = [];

    /// <summary>Recipients who have HidePushContent set.</summary>
    public IReadOnlySet<string> HideContentForUserIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}
