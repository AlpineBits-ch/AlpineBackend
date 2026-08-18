namespace Guild.Contracts;

/// <summary>
/// Represents a single, discrete permission that an external service can query against a
/// user/channel pair.
/// </summary>
public enum ExternalPermission
{
    // ── Message permissions ──────────────────────────────────────────────────

    /// <summary>Read messages and view the channel.</summary>
    ViewChannel = 0,

    /// <summary>Send new messages into the channel.</summary>
    SendMessages = 1,

    /// <summary>Edit messages the user themselves sent.</summary>
    EditOwnMessages = 2,

    /// <summary>Edit any message in the channel (moderator action).</summary>
    EditAnyMessage = 3,

    /// <summary>Delete messages the user themselves sent.</summary>
    DeleteOwnMessages = 4,

    /// <summary>Delete any message in the channel (moderator action).</summary>
    DeleteAnyMessage = 5,

    /// <summary>Pin or unpin messages in the channel.</summary>
    PinMessages = 6,

    // ── Attachment / embed permissions ───────────────────────────────────────

    /// <summary>Upload files and attachments.</summary>
    AttachFiles = 7,

    /// <summary>Post URLs that unfurl into embeds.</summary>
    EmbedLinks = 8,

    // ── Reaction permissions ─────────────────────────────────────────────────

    /// <summary>Add emoji reactions to messages.</summary>
    AddReactions = 9,

    // ── Voice / media permissions ────────────────────────────────────────────

    /// <summary>Connect to and listen in a voice channel.</summary>
    Connect = 10,

    /// <summary>Transmit audio in a voice channel.</summary>
    Speak = 11,

    /// <summary>Share a video stream or screen in a voice channel.</summary>
    Stream = 12,

    /// <summary>Mute other members in a voice channel (moderator action).</summary>
    MuteMembers = 13,

    /// <summary>Deafen other members in a voice channel (moderator action).</summary>
    DeafenMembers = 14,

    /// <summary>Move other members between voice channels (moderator action).</summary>
    MoveMembers = 15,

    // ── Thread permissions ───────────────────────────────────────────────────

    /// <summary>Create a new public thread inside the channel.</summary>
    CreateThreads = 16,

    /// <summary>Send messages inside an existing thread.</summary>
    SendMessagesInThreads = 17,

    /// <summary>Archive or unarchive threads the user owns.</summary>
    ManageOwnThreads = 18,

    /// <summary>Archive, unarchive, or delete any thread (moderator action).</summary>
    ManageAnyThread = 19,

    // ── Moderation permissions ───────────────────────────────────────────────

    /// <summary>Manage channel-level settings: name, topic, slowmode, overwrites.</summary>
    ManageChannel = 20,

    /// <summary>Grant or revoke permissions for this channel specifically.</summary>
    ManagePermissions = 21,

    CreateInvite = 22,

    // ── Moderation (members) ─────────────────────────────────────────────────

    /// <summary>Remove a member from the guild; they may rejoin with a new invite.</summary>
    KickMembers = 23,

    /// <summary>Remove a member from the guild and block them from rejoining.</summary>
    BanMembers = 24,

    /// <summary>Temporarily restrict a member from sending messages/reacting/connecting (timeout).</summary>
    ModerateMembers = 25,

    /// <summary>Manage guild-level settings: name, description, deletion.</summary>
    ManageGuild = 26,

    /// <summary>View the guild's moderation audit log.</summary>
    ViewAuditLog = 27,

    // ── Customization ────────────────────────────────────────────────────────

    /// <summary>Create, rename, or delete the guild's custom emoji.</summary>
    ManageEmojis = 28,

    /// <summary>Create, update, or cancel the guild's scheduled events.</summary>
    ManageEvents = 29,

    // 30-40 are retired: the household modules that moved to ExternalModulePermission.

    // ── Catch-all ────────────────────────────────────────────────────────────

    /// <summary>Superadmin / guild owner.</summary>
    Superadmin = 41,

    /// <summary>Ping the whole channel with @everyone / @here.</summary>
    MentionEveryone = 42,

    /// <summary>Create, edit, delete and reorder the guild's roles.</summary>
    ManageRoles = 43,

    /// <summary>Create, edit, delete and regenerate the token of a channel webhook.</summary>
    ManageWebhooks = 44,

    /// <summary>Change your own nickname in this guild.</summary>
    ChangeNickname = 45,

    /// <summary>Change other members' nicknames (also subject to the role hierarchy).</summary>
    ManageNicknames = 46,

    // 47-50 are retired: the wave-two household modules, likewise moved.

    // ── Discord parity ───────────────────────────────────────────────────────

    /// <summary>Read messages sent before the user arrived.</summary>
    ReadMessageHistory = 51,

    /// <summary>Invoke an installed bot's slash commands.</summary>
    UseApplicationCommands = 52,

    /// <summary>React with, and send, emoji from other guilds.</summary>
    UseExternalEmojis = 53,

    /// <summary>Send stickers from other guilds.</summary>
    UseExternalStickers = 54,

    /// <summary>Speak at a volume that ducks everyone else in a voice channel.</summary>
    PrioritySpeaker = 55,

    /// <summary>Raise a hand in a stage-style channel where Speak is withheld.</summary>
    RequestToSpeak = 56,

    /// <summary>Transmit using voice activity detection rather than push-to-talk.</summary>
    UseVoiceActivity = 57,

    /// <summary>Post a voice message.</summary>
    SendVoiceMessages = 58,

    /// <summary>Create a poll in a channel.</summary>
    SendPolls = 59,

    /// <summary>Upload an emoji, sticker or soundboard sound.</summary>
    CreateExpressions = 60,

    /// <summary>Edit and delete anyone's emoji, sticker or soundboard sound.</summary>
    ManageExpressions = 61,

    /// <summary>Create a thread only its invited members can see.</summary>
    CreatePrivateThreads = 62,
}

/// <summary>
/// The <see cref="Guild.Domain.Enums.ModulePermissions"/> mirror: permissions belonging to an
/// optional guild module (the wiki, the household modules) rather than to every guild.
/// </summary>
public enum ExternalModulePermission
{
    // ── Wiki ─────────────────────────────────────────────────────────────────

    /// <summary>Read the guild's wiki.</summary>
    ViewWiki = 0,

    /// <summary>Create a new wiki page.</summary>
    CreateWikiPages = 1,

    /// <summary>Edit wiki pages the user themselves authored.</summary>
    EditOwnWikiPages = 2,

    /// <summary>Edit anyone's wiki page.</summary>
    EditAnyWikiPage = 3,

    /// <summary>Delete a wiki page.</summary>
    DeleteWikiPages = 4,

    /// <summary>Restore or prune a wiki page's revision history.</summary>
    ManageWikiRevisions = 5,

    /// <summary>Create, rename and reorder wiki categories.</summary>
    ManageWikiStructure = 6,

    /// <summary>Remove anyone's wiki comment.</summary>
    ModerateWikiComments = 7,

    /// <summary>Publish a wiki page outside the guild.</summary>
    PublishWikiPublicly = 8,

    // ── Household modules ────────────────────────────────────────────────────

    /// <summary>Rename or clear a list, and delete anyone's item.</summary>
    ManageLists = 9,

    /// <summary>Add items to a list.</summary>
    AddListItems = 10,

    /// <summary>Tick items off a list.</summary>
    CheckOffListItems = 11,

    /// <summary>Create, edit and delete chores; set effort weights and rotations.</summary>
    ManageChores = 12,

    /// <summary>Mark a chore occurrence as done or skipped.</summary>
    CompleteChores = 13,

    /// <summary>Edit or delete any expense, and record settlements between other members.</summary>
    ManageLedger = 14,

    /// <summary>Add an expense you paid for.</summary>
    AddExpenses = 15,

    /// <summary>Add, edit and delete pantry items and their low-stock thresholds.</summary>
    ManagePantry = 16,

    /// <summary>Open a house decision.</summary>
    CreateDecisions = 17,

    /// <summary>Support, abstain on, or block a house decision.</summary>
    VoteDecisions = 18,

    /// <summary>Grant and revoke time-boxed guest roles.</summary>
    ManageGuests = 19,

    /// <summary>Add a recipe and put something on the meal plan.</summary>
    PlanMeals = 20,

    /// <summary>Edit or delete anyone's recipe or planned meal.</summary>
    ManageMeals = 21,

    /// <summary>Record that something was serviced or repaired, and flag an appliance broken.</summary>
    LogMaintenance = 22,

    /// <summary>Add, edit and delete maintenance assets and anyone's log entry.</summary>
    ManageMaintenance = 23,

    // ── Roleplay ─────────────────────────────────────────────────────────────

    /// <summary>Speak as a persona in this guild, and adopt one into it.</summary>
    UsePersonas = 24,

    /// <summary>Create and edit the guild's own shared personas, and edit anyone's profile here.</summary>
    ManageAnyPersona = 25,

    /// <summary>Approve a character, or send it back with a reason.</summary>
    ApprovePersonas = 26,

    /// <summary>Open, pause and conclude a scene, and set or skip whose turn it is.</summary>
    ManageScenes = 27,

    /// <summary>Roll dice into a channel.</summary>
    RollDice = 28,

    /// <summary>Roll where the result is not public.</summary>
    RollHidden = 29,

    /// <summary>Export a scene or a campaign as a readable document.</summary>
    ExportChronicle = 30,
}
 
// ─────────────────────────────────────────────────────────────────────────────
 