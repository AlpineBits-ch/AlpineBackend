namespace Guild.Domain.Enums;

/// <summary>What kind of broadcast ping a <see cref="Entity.ChannelBroadcastMention"/> records.
/// Stored as a plain integer, like NotificationLevel and for the same reason - it is only ever read
/// through EF, so it does not need to be a Postgres enum type.</summary>
public enum BroadcastMentionKind
{
    /// <summary>`@everyone`. Reaches every member who joined before it was sent.</summary>
    Everyone,

    /// <summary>`@role`.</summary>
    Role,
}
