using Discovery.Domain.Topics;
using Persistence;

namespace Discovery.Domain.Entities;

public enum ListingState { Draft, Published, Suspended, Unlisted }

public enum JoinPolicy { Open, Application }

public enum SuspensionReason { PlanLapsed, StaffAction }

public class Listing : BaseEntity<Listing>, IPrefixedEntity
{
    public static string Prefix { get; } = "disc";

    /// <summary>Cooldown between bumps. Spec section 9.1.</summary>
    public static readonly TimeSpan BumpCooldown = TimeSpan.FromHours(72);

    public string GuildId { get; set; } = null!;
    public string Headline { get; set; } = string.Empty;
    public string Pitch { get; set; } = string.Empty;
    public string Language { get; set; } = "en";
    public JoinPolicy JoinPolicy { get; set; } = JoinPolicy.Open;
    public List<string> Links { get; set; } = [];
    public ListingState State { get; set; } = ListingState.Draft;
    public SuspensionReason? SuspendedReason { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? LastBumpedAt { get; set; }

    public virtual ICollection<ListingTopic> Topics { get; set; } = new List<ListingTopic>();

    public static Listing Create(string guildId) =>
        new() { Id = GenerateId(), GuildId = guildId };

    public DateTimeOffset? BumpAvailableAt =>
        LastBumpedAt is null ? null : LastBumpedAt.Value + BumpCooldown;

    public void Publish(DateTimeOffset now)
    {
        State = ListingState.Published;
        SuspendedReason = null;
        // Keeps the original date: a guild that unlists for a month and comes back is not new.
        PublishedAt ??= now;
        LastBumpedAt = now;
    }

    public void Unlist()
    {
        if (State != ListingState.Published) return;
        State = ListingState.Unlisted;
        SuspendedReason = null;
    }

    public void Suspend(SuspensionReason reason)
    {
        if (State != ListingState.Published) return;
        State = ListingState.Suspended;
        SuspendedReason = reason;
    }

    public bool Bump(DateTimeOffset now)
    {
        if (State != ListingState.Published) return false;
        if (BumpAvailableAt is { } available && now < available) return false;
        LastBumpedAt = now;
        return true;
    }
}
