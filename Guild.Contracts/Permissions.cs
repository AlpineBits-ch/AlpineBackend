namespace Guild.Contracts;

/// <summary>
/// Represents a single, discrete permission that an external service can query against a
/// user/channel pair.
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
 
    /// <summary>Manage channel-level settings: name, topic, slowmode, overwrites.</summary>
    ManageChannel,
 
    /// <summary>Grant or revoke permissions for this channel specifically.</summary>
    ManagePermissions,
 
    CreateInvite,

    // ── Moderation (members) ─────────────────────────────────────────────────

    /// <summary>Remove a member from the guild; they may rejoin with a new invite.</summary>
    KickMembers,

    /// <summary>Remove a member from the guild and block them from rejoining.</summary>
    BanMembers,

    /// <summary>Temporarily restrict a member from sending messages/reacting/connecting (timeout).</summary>
    ModerateMembers,

    /// <summary>Manage guild-level settings: name, description, deletion.</summary>
    ManageGuild,

    /// <summary>View the guild's moderation audit log.</summary>
    ViewAuditLog,

    // ── Customization ────────────────────────────────────────────────────────

    /// <summary>Create, rename, or delete the guild's custom emoji.</summary>
    ManageEmojis,

    /// <summary>Create, update, or cancel the guild's scheduled events.</summary>
    ManageEvents,

    // ── Household modules ──────────────────────────────────────────────────── Each of these is
    // unavailable - for everyone, owner included - unless the guild has the corresponding
    // GuildFeatures module enabled.

    /// <summary>Rename or clear a list, and delete anyone's item.</summary>
    ManageLists,

    /// <summary>Add items to a list.</summary>
    AddListItems,

    /// <summary>Tick items off a list.</summary>
    CheckOffListItems,

    /// <summary>Create, edit and delete chores; set effort weights and rotations.</summary>
    ManageChores,

    /// <summary>Mark a chore occurrence as done or skipped.</summary>
    CompleteChores,

    /// <summary>Edit or delete any expense, and record settlements between other members.</summary>
    ManageLedger,

    /// <summary>Add an expense you paid for.</summary>
    AddExpenses,

    /// <summary>Add, edit and delete pantry items and their low-stock thresholds.</summary>
    ManagePantry,

    /// <summary>Open a house decision.</summary>
    CreateDecisions,

    /// <summary>Support, abstain on, or block a house decision.</summary>
    VoteDecisions,

    /// <summary>Grant and revoke time-boxed guest roles.</summary>
    ManageGuests,

    // ── Catch-all ────────────────────────────────────────────────────────────
 
    /// <summary>Superadmin / guild owner.</summary>
    Superadmin,
}
 
// ─────────────────────────────────────────────────────────────────────────────
 