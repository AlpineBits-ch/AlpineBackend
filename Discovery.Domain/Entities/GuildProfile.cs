using Persistence;

namespace Discovery.Domain.Entities;

/// <summary>Guild identity for a card. Never authoritative: refreshed on a TTL, so a rename shows
/// late. Display only.</summary>
public class GuildProfile : BaseEntity<GuildProfile>, IPrefixedEntity
{
    public static string Prefix { get; } = "gpfl";

    public string GuildId { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string? IconUrl { get; set; }
    public string? BannerUrl { get; set; }
    public int MemberCount { get; set; }
    public int ActiveMemberCount { get; set; }
    public string Features { get; set; } = string.Empty;
    public DateTimeOffset ProjectedAt { get; set; }
}
