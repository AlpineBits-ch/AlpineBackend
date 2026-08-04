using Persistence;

namespace Guild.Domain.Entity;

/// <summary>
/// "Do I accept direct messages from the people I share this server with?" - the per-guild half of
/// the DM policy (privacy spec T2-14), and the control Discord users reach for most.
/// </summary>
public class GuildDirectMessagePreference : BaseEntity<GuildDirectMessagePreference>, IPrefixedEntity
{
    public static string Prefix { get; } = "gdmp";

    public string UserId { get; set; } = null!;

    public string GuildId { get; set; } = null!;
    public virtual Aggregates.Guild Guild { get; set; } = null!;

    /// <summary>The stored override.</summary>
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
