namespace Guild.Domain.Enums;

/// <summary>
/// The core permission mask: the capabilities every guild has, regardless of which <see
/// cref="GuildFeatures"/> modules it has switched on.
/// </summary>
[Flags]
public enum Permissions : ulong
{
    None                    = 0,

    // ── Channel visibility ───────────────────────────────────────────────────
    ViewChannel             = 1ul << 0,

    // ── Message permissions ──────────────────────────────────────────────────
    SendMessages            = 1ul << 1,
    EditOwnMessages         = 1ul << 2,
    EditAnyMessage          = 1ul << 3,
    DeleteOwnMessages       = 1ul << 4,
    DeleteAnyMessage        = 1ul << 5,
    PinMessages             = 1ul << 6,

    // ── Attachment / embed permissions ───────────────────────────────────────
    AttachFiles             = 1ul << 7,
    EmbedLinks              = 1ul << 8,

    // ── Reaction permissions ─────────────────────────────────────────────────
    AddReactions            = 1ul << 9,

    // ── Voice / media permissions ────────────────────────────────────────────
    Connect                 = 1ul << 10,
    Speak                   = 1ul << 11,
    Stream                  = 1ul << 12,
    MuteMembers             = 1ul << 13,
    DeafenMembers           = 1ul << 14,
    MoveMembers             = 1ul << 15,

    // ── Thread permissions ───────────────────────────────────────────────────
    /// <summary>Start a thread anyone who can see the parent channel can see.</summary>
    CreateThreads           = 1ul << 16,
    SendMessagesInThreads   = 1ul << 17,
    ManageOwnThreads        = 1ul << 18,
    ManageAnyThread         = 1ul << 19,

    // ── Moderation permissions ───────────────────────────────────────────────
    ManageChannel           = 1ul << 20,
    ManagePermissions       = 1ul << 21,

    // ── General ──────────────────────────────────────────────────────────────
    CreateInvite            = 1ul << 22,

    // ── Discord parity: reading, posting, expressions ────────────────────────

    /// <summary>Read messages sent before you arrived - the channel backlog.</summary>
    ReadMessageHistory      = 1ul << 23,

    /// <summary>Post a voice message rather than typed text.</summary>
    SendVoiceMessages       = 1ul << 24,

    /// <summary>Create a poll in a channel.</summary>
    SendPolls               = 1ul << 25,

    /// <summary>React with, and send, emoji belonging to a different guild.</summary>
    UseExternalEmojis       = 1ul << 26,

    /// <summary>The same grant as <see cref="UseExternalEmojis"/> for stickers, which are larger
    /// and harder to skim past, and are therefore gated separately.</summary>
    UseExternalStickers     = 1ul << 27,

    /// <summary>Start a thread only its invited members can see.</summary>
    CreatePrivateThreads    = 1ul << 28,

    /// <summary>Invoke an installed bot's slash commands.</summary>
    UseApplicationCommands  = 1ul << 29,

    /// <summary>Upload your own emoji, sticker or soundboard sound.</summary>
    CreateExpressions       = 1ul << 30,

    /// <summary>
    /// Edit and delete anyone's emoji, sticker or soundboard sound - the full curation grant,
    /// matching Discord's MANAGE_GUILD_EXPRESSIONS.
    /// </summary>
    ManageExpressions       = 1ul << 31,

    // ── Moderation (members) ─────────────────────────────────────────────────
    KickMembers             = 1ul << 32,
    BanMembers              = 1ul << 33,
    ModerateMembers         = 1ul << 34,
    ManageGuild             = 1ul << 35,
    ViewAuditLog            = 1ul << 36,

    // ── Customization ────────────────────────────────────────────────────────
    /// <summary>Create, rename or delete the guild's custom emoji.</summary>
    ManageEmojis            = 1ul << 37,
    ManageEvents            = 1ul << 38,

    // ── Discord parity: voice ────────────────────────────────────────────────

    /// <summary>Speak at a volume that ducks everyone else in the channel.</summary>
    PrioritySpeaker         = 1ul << 39,

    /// <summary>Raise a hand in a stage-style channel where <see cref="Speak"/> is withheld from
    /// the audience. Without it, a listen-only channel has no in-band way for a listener to ask
    /// to be promoted, and the ask has to happen somewhere else entirely.</summary>
    RequestToSpeak          = 1ul << 40,

    /// <summary>Transmit using voice activity detection rather than push-to-talk.</summary>
    UseVoiceActivity        = 1ul << 41,

    // Bits 42-49 and 55-62 are free. See the enum-level remarks before using one.

    // ── Mentions ─────────────────────────────────────────────────────────────
    /// <summary>Ping the whole channel with @everyone / @here.</summary>
    MentionEveryone         = 1ul << 50,

    // ── Moderation (split out of coarser bits) ───────────────────────────────
    /// <summary>Create, edit, delete and reorder the guild's roles.</summary>
    ManageRoles             = 1ul << 51,

    /// <summary>Create, edit, delete and regenerate the token of a channel webhook.</summary>
    ManageWebhooks          = 1ul << 52,

    // ── Nicknames ────────────────────────────────────────────────────────────
    /// <summary>Change your own nickname in this guild. Part of the @everyone defaults.</summary>
    ChangeNickname          = 1ul << 53,

    /// <summary>Change other members' nicknames.</summary>
    ManageNicknames         = 1ul << 54,

    // ── Catch-all ────────────────────────────────────────────────────────────
    Superadmin              = 1ul << 63,
}

public static class PermissionExtensions
{
    public static bool HasPermission(this Permissions permissions, Permissions requiredPermission)
    {
        if (permissions.HasFlag(Permissions.Superadmin))
        {
            return true;
        }

        return (permissions & requiredPermission) == requiredPermission;
    }
}
