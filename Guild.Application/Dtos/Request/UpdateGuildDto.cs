using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

public class UpdateGuildDto
{
    public string Name { get; set; }
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

    /// <summary>Explicit module set. Disabling a module hides it and strips its permissions but
    /// never deletes its data, so this is always reversible.</summary>
    public GuildFeatures? Features { get; set; }

    /// <summary>What members who have set no preference of their own fall back to. Null/omitted
    /// leaves it untouched. Only AllMessages and OnlyMentions are accepted, matching Discord's
    /// default_message_notifications - Nothing as a *guild-wide* default would silence a server for
    /// everyone who never opened its settings, which is not a thing an admin should be able to do
    /// on their behalf.</summary>
    public NotificationLevel? DefaultMessageNotifications { get; set; }
}
