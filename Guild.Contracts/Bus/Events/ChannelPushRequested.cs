namespace Guild.Contracts.Bus.Events;

/// <summary>
/// "These users should get a push for this channel message." Published by Guild (which owns
/// membership, presence and the notification settings needed to decide who) and consumed by
/// Messaging (which owns the Firebase credentials and the existing push plumbing, so it decides
/// how).
/// </summary>
public class ChannelPushRequested
{
    public string GuildId { get; set; } = null!;
    public string ChannelId { get; set; } = null!;
    public string MessageId { get; set; } = null!;

    /// <summary>Who to notify.</summary>
    public List<string> UserIds { get; set; } = [];

    /// <summary>Author, for the notification title.</summary>
    public string AuthorId { get; set; } = null!;

    /// <summary>
    /// The stored body: readable text for a plaintext message, the base64 MLS message for an
    /// encrypted one.
    /// </summary>
    public byte[] Content { get; set; } = [];

    public bool IsEncrypted { get; set; }

    /// <summary>Which MLS group generation the ciphertext was sealed under; null on plaintext.
    /// Without it the device cannot tell which group to decrypt against once a channel's
    /// encryption has been toggled off and on.</summary>
    public int? MlsGeneration { get; set; }
}
