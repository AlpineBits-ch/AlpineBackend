using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

public class UpdateGuildDto
{
    /// <summary>Null/omitted leaves the name alone, so a request that only flips a module cannot
    /// blank it. A guild has to have a name, so there is no way to clear one.</summary>
    public string? Name { get; set; }

    /// <summary>Null/omitted leaves the description alone; an empty string clears it.</summary>
    public string? Description { get; set; }

    /// <summary>Channel that receives join/leave system messages - Overview-style setting in
    /// Discord. Null/omitted leaves the guild's current system channel untouched (not cleared),
    /// so older clients that don't send this field can't accidentally unset it.</summary>
    public string? SystemChannelId { get; set; }

    /// <summary>Null/omitted leaves the guild's current verification level untouched.</summary>
    public GuildVerificationLevel? VerificationLevel { get; set; }

    /// <summary>Changing the kind re-seeds <see cref="Features"/> from the new kind's preset,
    /// unless Features is sent in the same request - so "switch this server to a household"
    /// is one call, while "switch it and keep exactly these modules" is still possible.</summary>
    public GuildKind? Kind { get; set; }

    /// <summary>Explicit module set.</summary>
    public GuildFeatures? Features { get; set; }

    /// <summary>What members who have set no preference of their own fall back to.</summary>
    public NotificationLevel? DefaultMessageNotifications { get; set; }
}
