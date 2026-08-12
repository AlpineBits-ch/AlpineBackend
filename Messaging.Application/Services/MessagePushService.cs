using System.Text;
using System.Text.Json;
using FirebaseAdmin.Messaging;

namespace Messaging.Application.Services;

/// <summary>
/// One message notification, before it is turned into a per-recipient FCM message.
/// </summary>
public class MessagePushPayload
{
    public required string MessageId { get; init; }

    /// <summary>Conversation id or channel id - whichever addresses the MLS group.</summary>
    public required string ContextId { get; init; }

    public string? ConversationId { get; init; }
    public string? ChannelId { get; init; }
    public string? GuildId { get; init; }

    public required string AuthorId { get; init; }
    public required string SenderName { get; init; }

    /// <summary>Public avatar URL.</summary>
    public string? SenderAvatarUrl { get; init; }

    public bool IsEncrypted { get; init; }

    /// <summary>The stored message body.</summary>
    public byte[] Content { get; init; } = [];

    /// <summary>Which MLS group generation the ciphertext was sealed under.</summary>
    public int? MlsGeneration { get; init; }

    /// <summary>
    /// T2-23. Recipients who have <c>HidePushContent</c> set: their copy of this push carries no
    /// body, no author name, no avatar and no ciphertext - only the routing ids the client needs to
    /// open the right conversation once the user unlocks the phone.
    /// </summary>
    public IReadOnlySet<string> HideContentForUserIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// Builds and sends the phone notification for a new message.
///
/// Two shapes come out of one <see cref="Message"/>, because the platforms need opposite things to
/// decrypt in the background:
///
/// * <b>Android</b> gets a data-only, high-priority message. A `notification` block would have the
///   OS draw the tray entry itself - showing ciphertext, or a placeholder that can never be
///   corrected - and would stop the Flutter background isolate from being the thing that decides
///   what the notification says. Data-only hands that decision to `MessagePushDecryptor`.
/// * <b>iOS</b> gets an APNs alert with `mutable-content`, because a silent push is not
///   guaranteed delivery there and a killed app would simply miss messages. The alert is the
///   placeholder; the Notification Service Extension replaces its body with the decrypted text.
///
/// Both configs ride on the same message: a push token says only "FCM", not which platform issued
/// it, and each side ignores the other's block.
/// </summary>
public static class MessagePushService
{
    /// <summary>Shown until (or unless) the client decrypts.</summary>
    public const string EncryptedPlaceholder = "You have a new encrypted message";

    /// <summary>T2-23's body for a recipient who hides push content.</summary>
    public const string HiddenContentPlaceholder = "You have a new message";

    /// <summary>The APNs alert title for a hidden push.</summary>
    public const string HiddenContentTitle = "New message";

    /// <summary>FCM caps a message's data payload at 4KB.</summary>
    private const int MaxCiphertextChars = 3000;

    /// <summary>Truncation guard for a plaintext body, which is otherwise capped only by the
    /// message length limit. A notification shows two lines; nobody loses anything real here.</summary>
    private const int MaxBodyChars = 500;

    public static async Task SendAsync(
        IEnumerable<(string Token, string UserId)> recipients,
        MessagePushPayload payload,
        ILogger? logger = null)
    {
        foreach (var (token, userId) in recipients)
        {
            // Per recipient, not per message: the alert body and title depend on whether *this*
            // reader hides push content, so they cannot be computed once outside the loop the way
            // they used to be.
            var hidden = payload.HideContentForUserIds.Contains(userId);
            var body = hidden ? HiddenContentPlaceholder : BuildBody(payload, out _);
            var title = hidden ? HiddenContentTitle : payload.SenderName;

            var data = BuildData(payload, userId);
            try
            {
                await FirebaseMessaging.DefaultInstance.SendAsync(new Message
                {
                    Token = token,
                    Data = data,
                    // Deliberately no top-level Notification - see the class remarks.
                    Android = new AndroidConfig
                    {
                        // Without this a data-only message is held until the device leaves Doze,
                        // which for a chat notification means "arrives tomorrow morning".
                        Priority = Priority.High,
                    },
                    Apns = new ApnsConfig
                    {
                        Headers = new Dictionary<string, string>
                        {
                            ["apns-priority"] = "10",
                            // Required for mutable-content to be honoured; without it APNs may
                            // coalesce the push and the extension never runs.
                            ["apns-push-type"] = "alert",
                        },
                        Aps = new Aps
                        {
                            Alert = new ApsAlert { Title = title, Body = body },
                            Sound = "default",
                            MutableContent = true,
                        },
                    },
                });
            }
            catch (Exception e)
            {
                // One dead token must not cost the other recipients their notification.
                logger?.LogWarning(e, "Message push failed for message {MessageId}", payload.MessageId);
            }
        }
    }

    /// <summary>
    /// The visible body, plus the ciphertext to ship alongside it (null when there is none to
    /// ship, or it did not fit).
    /// </summary>
    private static string BuildBody(MessagePushPayload payload, out string? ciphertext)
    {
        if (!payload.IsEncrypted)
        {
            ciphertext = null;
            return Truncate(Encoding.UTF8.GetString(payload.Content), MaxBodyChars);
        }

        // Already base64 on the wire: the client encrypts, base64s, and posts that string, which is
        // stored as its UTF-8 bytes.
        var encoded = Encoding.UTF8.GetString(payload.Content);
        ciphertext = encoded.Length is > 0 and <= MaxCiphertextChars ? encoded : null;
        return EncryptedPlaceholder;
    }

    /// <summary>The FCM data payload one recipient receives.</summary>
    public static Dictionary<string, string> BuildData(
        MessagePushPayload payload,
        string recipientUserId)
    {
        // T2-23. A recipient who hides push content gets routing ids and nothing else: no body, no
        // author name, no avatar, and no ciphertext either - shipping the ciphertext would let the
        // notification extension reconstruct the body on the lock screen, which is the exact thing
        // the setting exists to prevent.
        if (payload.HideContentForUserIds.Contains(recipientUserId))
        {
            var hiddenData = new Dictionary<string, string>
            {
                ["type"] = "message",
                ["messageId"] = payload.MessageId,
                ["contextId"] = payload.ContextId,
                ["recipientUserId"] = recipientUserId,
                ["encrypted"] = payload.IsEncrypted ? "1" : "0",
                ["hidden"] = "1",
                ["body"] = HiddenContentPlaceholder,
            };

            if (!string.IsNullOrWhiteSpace(payload.ConversationId)) hiddenData["conversationId"] = payload.ConversationId!;
            if (!string.IsNullOrWhiteSpace(payload.ChannelId)) hiddenData["channelId"] = payload.ChannelId!;
            if (!string.IsNullOrWhiteSpace(payload.GuildId)) hiddenData["guildId"] = payload.GuildId!;

            return hiddenData;
        }

        var body = BuildBody(payload, out var ciphertext);

        var data = new Dictionary<string, string>
        {
            ["type"] = "message",
            ["messageId"] = payload.MessageId,
            ["contextId"] = payload.ContextId,
            ["authorId"] = payload.AuthorId,
            ["senderName"] = payload.SenderName,
            // Which account on the handset this is for.
            ["recipientUserId"] = recipientUserId,
            ["encrypted"] = payload.IsEncrypted ? "1" : "0",
            ["body"] = body,
        };

        if (!string.IsNullOrWhiteSpace(payload.ConversationId)) data["conversationId"] = payload.ConversationId!;
        if (!string.IsNullOrWhiteSpace(payload.ChannelId)) data["channelId"] = payload.ChannelId!;
        if (!string.IsNullOrWhiteSpace(payload.GuildId)) data["guildId"] = payload.GuildId!;
        if (!string.IsNullOrWhiteSpace(payload.SenderAvatarUrl)) data["senderAvatarUrl"] = payload.SenderAvatarUrl!;
        if (payload.MlsGeneration is { } generation) data["mlsGeneration"] = generation.ToString();

        if (payload.IsEncrypted)
        {
            if (ciphertext is not null) data["ciphertext"] = ciphertext;
            // Says "there was ciphertext and it did not fit", as opposed to "there is none".
            else data["truncated"] = "1";
        }

        return data;
    }

    /// <summary>
    /// The Web Push payload for one recipient: the same object <see cref="BuildData"/> produces,
    /// serialised as JSON.
    /// </summary>
    /// <param name="maxBytes">
    /// Budget for the serialised JSON - <c>WebPushEncryption.MaxPayloadBytes</c> at the call site.
    /// </param>
    public static string BuildWebPushPayload(MessagePushPayload payload, string recipientUserId, int maxBytes)
    {
        var data = BuildData(payload, recipientUserId);
        var json = JsonSerializer.Serialize(data);
        if (Encoding.UTF8.GetByteCount(json) <= maxBytes) return json;

        if (data.Remove("ciphertext")) data["truncated"] = "1";

        json = JsonSerializer.Serialize(data);
        if (Encoding.UTF8.GetByteCount(json) <= maxBytes) return json;

        // Still over budget with no ciphertext in it: something unbounded that is not the
        // ciphertext - a very long plaintext body - so trim that too rather than send nothing.
        data["body"] = HiddenContentPlaceholder;
        data.Remove("senderAvatarUrl");
        return JsonSerializer.Serialize(data);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
