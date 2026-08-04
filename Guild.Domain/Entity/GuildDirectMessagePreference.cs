using Persistence;

namespace Guild.Domain.Entity;

/// <summary>
/// "Do I accept direct messages from the people I share <i>this</i> server with?" - the per-guild
/// half of the DM policy (privacy spec T2-14), and the control Discord users reach for most.
///
/// <para>Only meaningful under the global <c>DirectMessagePolicy.FriendsAndServerMembers</c> branch:
/// a friend may DM regardless of any row here, and a policy of <c>Nobody</c> refuses before this is
/// consulted. Messaging owns that resolution and asks Guild for these rows over
/// <c>GetGuildDirectMessagePreferenceRequest</c>.</para>
///
/// <para><b>Keyed on (UserId, GuildId), not on GuildMember.</b> The preference is a statement about
/// a person and a server, and it has to outlive a membership row: someone who leaves a server after
/// turning DMs off there, and is re-invited a week later, must not silently come back opted in.
/// That is the opposite of the choice <see cref="GuildNotificationSetting"/> makes, and deliberately
/// so - notification settings are worthless without the membership, this one is a safety control.
/// The FK to Guild still cascades, because a deleted guild has no shared members left to gate.</para>
///
/// <para>Absence of a row is not "no opinion is recorded, so allow". It resolves against the user's
/// global policy - see <c>GuildDirectMessagePreferenceService.DefaultFor</c> - and when that policy
/// cannot be reached at all, it resolves to <c>false</c>. Fail closed.</para>
/// </summary>
public class GuildDirectMessagePreference : BaseEntity<GuildDirectMessagePreference>, IPrefixedEntity
{
    public static string Prefix { get; } = "gdmp";

    public string UserId { get; set; } = null!;

    public string GuildId { get; set; } = null!;
    public virtual Aggregates.Guild Guild { get; set; } = null!;

    /// <summary>The stored override. Only ever written by the owning user - there is no permission
    /// that lets a moderator set this for someone else, by design.</summary>
    public bool AllowDirectMessages { get; set; } = true;

    public static GuildDirectMessagePreference Create(string userId, string guildId, bool allowDirectMessages)
    {
        var now = DateTimeOffset.UtcNow;
        return new GuildDirectMessagePreference
        {
            Id = GenerateId(),
            CreatedAt = now,
            UpdatedAt = now,
            UserId = userId,
            GuildId = guildId,
            AllowDirectMessages = allowDirectMessages,
        };
    }
}
