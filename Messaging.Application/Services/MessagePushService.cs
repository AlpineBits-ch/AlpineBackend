using System.Text;
using FirebaseAdmin.Messaging;

namespace Messaging.Application.Services;

/// <summary>
/// One message notification, before it is turned into a per-recipient FCM message.
/// </summary>
public class MessagePushPayload
{
    public required string MessageId { get; init; }

    /// <summary>Conversation id or channel id - whichever addresses the MLS group. Named the same
    /// as the client's `contextId` because it is the same value.</summary>
    public required string ContextId { get; init; }

    public string? ConversationId { get; init; }
    public string? ChannelId { get; init; }
    public string? GuildId { get; init; }

    public required string AuthorId { get; init; }
    public required string SenderName { get; init; }

    /// <summary>Public avatar URL. Unauthenticated (it 302s to object storage), which is what lets
    /// a notification-service extension and an FCM background isolate fetch it without a session.</summary>
    public string? SenderAvatarUrl { get; init; }

    public bool IsEncrypted { get; init; }

    /// <summary>The stored message body. For a plaintext message this is the text; for an
    /// encrypted one it is the base64 MLS message exactly as the sending client produced it -
    /// the client stores base64 in a UTF-8 content column, so no re-encoding happens here.</summary>
    public byte[] Content { get; init; } = [];

    /// <summary>Which MLS group generation the ciphertext was sealed under. The client needs it to
    /// pick the right group: encryption can be toggled off and on, and each stretch is a distinct
    /// group whose epochs restart at zero.</summary>
    public int? MlsGeneration { get; init; }

    /// <summary>
    /// T2-23. Recipients who have <c>HidePushContent</c> set: their copy of this push carries no
    /// body, no author name, no avatar and no ciphertext - only the routing ids the client needs to
    /// open the right conversation once the user unlocks the phone.
    ///
    /// <para>Per recipient rather than per message, because it is the reader's setting and one
    /// message goes to several of them. Empty by default, so nothing changes for a caller that does
    /// not populate it.</para>
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
    /// <summary>Shown until (or unless) the client decrypts. Matches the copy the server used to
    /// send as the final body, so a device that cannot decrypt is no worse off than before.</summary>
    public const string EncryptedPlaceholder = "You have a new encrypted message";

    /// <summary>T2-23's body for a recipient who hides push content. The same mechanism as the
    /// encrypted placeholder - a fixed string in place of the message - but it does not claim the
    /// message was encrypted, because for a plaintext DM that would simply be untrue.</summary>
    public const string HiddenContentPlaceholder = "You have a new message";

    /// <summary>The APNs alert title for a hidden push. The title is normally the sender's name,
    /// which is exactly one of the three things T2-23 says must not be in the payload.</summary>
    public const string HiddenContentTitle = "New message";

    /// <summary>
    /// FCM caps a message's data payload at 4KB. The ciphertext is the only unbounded field, so it
    /// is the one that gets dropped when the budget runs out - the rest of the payload still buys
    /// the sender's name and avatar, and the client falls back to the placeholder body.
    ///
    /// Dropping it silently would be worse than useless, so `truncated` is set and the client can
    /// tell "no ciphertext was sent" apart from "this is a plaintext message".
    /// </summary>
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

        // Already base64 on the wire: the client encrypts, base64s, and posts that string, which
        // is stored as its UTF-8 bytes. Base64-ing it again here would hand the client something
        // openmls cannot parse.
        var encoded = Encoding.UTF8.GetString(payload.Content);
        ciphertext = encoded.Length is > 0 and <= MaxCiphertextChars ? encoded : null;
        return EncryptedPlaceholder;
    }

    /// <summary>
    /// The FCM data payload one recipient receives. Public because it is the actual contract with
    /// both clients - `MessagePushPayload.tryParse` in Dart and `NotificationService.swift` read
    /// exactly these keys - and the only part of this class that can be tested without Firebase.
    /// </summary>
    public static Dictionary<string, string> BuildData(
        MessagePushPayload payload,
        string recipientUserId)
    {
        // T2-23. A recipient who hides push content gets routing ids and nothing else: no body, no
        // author name, no avatar, and no ciphertext either - shipping the ciphertext would let the
        // notification extension reconstruct the body on the lock screen, which is the exact thing
        // the setting exists to prevent. `hidden` is set so the client can tell this apart from a
        // message that genuinely had no content.
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
            // Which account on the handset this is for. A phone can be signed into one account at
            // a time, but MLS state is stored per user id, and the extension/background isolate has
            // to name the directory before it can read a group out of it.
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
