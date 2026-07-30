namespace Guild.Domain.Enums;

/// <summary>How much a member wants to hear from a guild, category or channel. Ordered from
/// loudest to quietest so a numeric comparison reads naturally.
///
/// Unlike ChannelType/GuildKind/etc., this is deliberately NOT registered as a Npgsql enum type -
/// it is stored as a plain integer. Those enums are mapped by name because they are read in raw
/// SQL and appear in migrations; this one is only ever read through EF, and staying an integer
/// keeps it free of the append-only constraint a Postgres enum type imposes.</summary>
public enum NotificationLevel
{
    /// <summary>Notify on every message. Discord's default for a newly joined guild, and ours.</summary>
    AllMessages,

    /// <summary>Notify only when the member is mentioned directly, by a role they hold, or by
    /// @everyone/@here (unless those are separately suppressed).</summary>
    OnlyMentions,

    /// <summary>Never notify. Distinct from muting: "nothing" is a standing preference, a mute is
    /// a temporary silence with an expiry - a channel can be set to Nothing and separately muted,
    /// and unmuting returns it to Nothing rather than to AllMessages.</summary>
    Nothing,
}
