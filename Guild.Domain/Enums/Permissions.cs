namespace Guild.Domain.Enums;

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
    CreateThreads           = 1ul << 16,
    SendMessagesInThreads   = 1ul << 17,
    ManageOwnThreads        = 1ul << 18,
    ManageAnyThread         = 1ul << 19,
 
    // ── Moderation permissions ───────────────────────────────────────────────
    ManageChannel           = 1ul << 20,
    ManagePermissions       = 1ul << 21,
 
    // ── General Stuff that I forgot ──────────────────────────────────────────
    
    CreateInvite            = 1ul << 22,
    
    // -- WIKI
    
    ViewWiki                = 1ul << 23,
    CreateWikiPages             = 1ul << 24,
    EditOwnWikiPages            = 1ul << 25,
    EditAnyWikiPage             = 1ul << 26,
    DeleteWikiPages             = 1ul << 27,
    ManageWikiRevisions         = 1ul << 28,
    ManageWikiStructure         = 1ul << 29,
    ModerateWikiComments        = 1ul << 30,
    PublishWikiPublicly         = 1ul << 31,

    // ── Moderation (members) ─────────────────────────────────────────────────
    KickMembers             = 1ul << 32,
    BanMembers              = 1ul << 33,
    ModerateMembers         = 1ul << 34,
    ManageGuild             = 1ul << 35,
    ViewAuditLog            = 1ul << 36,

    // ── Customization ────────────────────────────────────────────────────────
    ManageEmojis            = 1ul << 37,
    ManageEvents            = 1ul << 38,

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