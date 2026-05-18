namespace Guild.Contracts;

/// <summary>
/// Represents a single, discrete permission that an external service can query
/// against a user/channel pair. Each value maps 1-to-1 to an internal
/// Guild.Domain.Enums.Permissions flag, keeping the public contract stable
/// even if the internal flag layout changes.
///
/// Values are kept as a plain enum (not [Flags]) because external services ask
/// "can this user do X?" one action at a time — they should never need to
/// compose bitfields themselves.
/// </summary>
public enum ExternalPermission
{
    // ── Message permissions ──────────────────────────────────────────────────
 
    /// <summary>Read messages and view the channel.</summary>
    ViewChannel,
 
    /// <summary>Send new messages into the channel.</summary>
    SendMessages,
 
    /// <summary>Edit messages the user themselves sent.</summary>
    EditOwnMessages,
 
    /// <summary>Edit any message in the channel (moderator action).</summary>
    EditAnyMessage,
 
    /// <summary>Delete messages the user themselves sent.</summary>
    DeleteOwnMessages,
 
    /// <summary>Delete any message in the channel (moderator action).</summary>
    DeleteAnyMessage,
 
    /// <summary>Pin or unpin messages in the channel.</summary>
    PinMessages,
 
    // ── Attachment / embed permissions ───────────────────────────────────────
 
    /// <summary>Upload files and attachments.</summary>
    AttachFiles,
 
    /// <summary>Post URLs that unfurl into embeds.</summary>
    EmbedLinks,
 
    // ── Reaction permissions ─────────────────────────────────────────────────
 
    /// <summary>Add emoji reactions to messages.</summary>
    AddReactions,
 
    // ── Voice / media permissions ────────────────────────────────────────────
 
    /// <summary>Connect to and listen in a voice channel.</summary>
    Connect,
 
    /// <summary>Transmit audio in a voice channel.</summary>
    Speak,
 
    /// <summary>Share a video stream or screen in a voice channel.</summary>
    Stream,
 
    /// <summary>Mute other members in a voice channel (moderator action).</summary>
    MuteMembers,
 
    /// <summary>Deafen other members in a voice channel (moderator action).</summary>
    DeafenMembers,
 
    /// <summary>Move other members between voice channels (moderator action).</summary>
    MoveMembers,
 
    // ── Thread permissions ───────────────────────────────────────────────────
 
    /// <summary>Create a new thread inside the channel.</summary>
    CreateThreads,
 
    /// <summary>Send messages inside an existing thread.</summary>
    SendMessagesInThreads,
 
    /// <summary>Archive or unarchive threads the user owns.</summary>
    ManageOwnThreads,
 
    /// <summary>Archive, unarchive, or delete any thread (moderator action).</summary>
    ManageAnyThread,
 
    // ── Moderation permissions ───────────────────────────────────────────────
 
    /// <summary>
    /// Manage channel-level settings: name, topic, slowmode, overwrites.
    /// Implies moderator-level access for this channel.
    /// </summary>
    ManageChannel,
 
    /// <summary>
    /// Grant or revoke permissions for this channel specifically.
    /// Subset of ManageChannel, surfaced separately so services can check
    /// whether a user can delegate access without checking full channel management.
    /// </summary>
    ManagePermissions,
 
    CreateInvite,
    
    // ── Catch-all ────────────────────────────────────────────────────────────
 
    /// <summary>
    /// Superadmin / guild owner. Grants all of the above unconditionally.
    /// External services should prefer checking the specific permission they
    /// actually need; this value exists so the owner short-circuit can be
    /// expressed in the contract without magic booleans.
    /// </summary>
    Superadmin,
}
 
// ─────────────────────────────────────────────────────────────────────────────
 