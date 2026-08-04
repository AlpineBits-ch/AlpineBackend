namespace Guild.Contracts.Bus.Events;

/// <summary>
/// "These users should get a push for this channel message."
///
/// Published by Guild (which owns membership, presence and the notification settings needed to
/// decide *who*) and consumed by Messaging (which owns the Firebase credentials and the existing
/// push plumbing, so it decides *how*). Splitting it this way keeps Firebase initialization in
/// exactly one service instead of a second copy in Guild, and mirrors how MessageCreatedForChannel
/// already crosses the same boundary in the other direction.
///
/// Recipients are fully resolved before publishing - this event is a send list, not a request to
/// work out a send list, so Messaging never needs to reach back into Guild's data.
/// </summary>
public class ChannelPushRequested
{
    public string GuildId { get; set; } = null!;
    public string ChannelId { get; set; } = null!;
    public string MessageId { get; set; } = null!;

    /// <summary>Who to notify. Already filtered by notification level, mute state, the mobile-push
    /// switch, and current connectedness.</summary>
    public List<string> UserIds { get; set; } = [];

    /// <summary>Author, for the notification title. Resolved to a display name by Messaging, which
    /// already makes that profile call for the DM push path.</summary>
    public string AuthorId { get; set; } = null!;

    /// <summary>The stored body: readable text for a plaintext message, the base64 MLS message for
    /// an encrypted one. Messaging shows the former directly and ships the latter to the device,
    /// which decrypts it in a notification-service extension (iOS) or an FCM background isolate
    /// (Android) - the server itself still cannot read it and still substitutes a placeholder for
    /// anyone whose device cannot.</summary>
    public byte[] Content { get; set; } = [];

    public bool IsEncrypted { get; set; }

    /// <summary>Which MLS group generation the ciphertext was sealed under; null on plaintext.
    /// Without it the device cannot tell which group to decrypt against once a channel's
    /// encryption has been toggled off and on.</summary>
    public int? MlsGeneration { get; set; }

    /// <summary>
    /// Every recipient on this event has <c>HidePushContent</c> set (privacy spec T2-23), so the
    /// push may carry routing ids only - no body, no author name, no channel name.
    ///
    /// <para>Guild splits one message's recipients into at most two events rather than putting a
    /// per-user flag inside one, because the decision is made once per cohort and the payload
    /// itself differs: on the hidden event <see cref="Content"/> is empty and
    /// <see cref="AuthorId"/> is blank, so the privacy property holds even for a consumer that
    /// ignores this flag entirely. What the flag adds is the ability to skip the profile lookup
    /// and render a generic body ("New message") instead of an empty one.</para>
    /// </summary>
    public bool HideContent { get; set; }
}
