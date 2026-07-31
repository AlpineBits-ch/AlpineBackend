using System.Text;
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
        var body = BuildBody(payload, out _);

        foreach (var (token, userId) in recipients)
        {
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
                            Alert = new ApsAlert { Title = payload.SenderName, Body = body },
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

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
