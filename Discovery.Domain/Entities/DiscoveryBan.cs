using Persistence;

namespace Discovery.Domain.Entities;

/// <summary>
/// A guild-keyed ban out of the discovery directory. Keyed on the guild, not the listing, because
/// <see cref="Listing.Publish"/> clears <see cref="Listing.SuspendedReason"/> from any state - a
/// listing-level ban would be undone the moment the owner presses publish again.
/// </summary>
public class DiscoveryBan : BaseEntity<DiscoveryBan>, IPrefixedEntity
{
    public static string Prefix { get; } = "dban";

    public string GuildId { get; set; } = null!;

    /// <summary>Written to be read by the owner.</summary>
    public string Reason { get; set; } = null!;

    /// <summary>Never leaves the console.</summary>
    public string? StaffNote { get; set; }

    public string BannedByUserId { get; set; } = null!;
    public DateTimeOffset BannedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LiftedAt { get; set; }
    public string? LiftedByUserId { get; set; }

    public bool IsActiveAt(DateTimeOffset now) =>
        LiftedAt is null && (ExpiresAt is null || ExpiresAt > now);

    public static DiscoveryBan Create(
        string guildId, string reason, string? staffNote, string byUserId,
        DateTimeOffset now, DateTimeOffset? expiresAt) =>
        new()
        {
            Id = GenerateId(),
            GuildId = guildId,
            Reason = reason,
            StaffNote = staffNote,
            BannedByUserId = byUserId,
            BannedAt = now,
            ExpiresAt = expiresAt,
        };
}
