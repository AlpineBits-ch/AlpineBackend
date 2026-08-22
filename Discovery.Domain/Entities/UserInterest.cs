using Discovery.Domain.Topics;
using Persistence;

namespace Discovery.Domain.Entities;

/// <summary>Where an interest came from. Suggested exists so the activity-detection prompt in spec
/// section 3.4 needs no migration.</summary>
public enum InterestSource { Manual, Suggested, Imported }

public class UserInterest : BaseEntity<UserInterest>, IPrefixedEntity
{
    public static string Prefix { get; } = "intr";

    public string UserId { get; set; } = null!;
    public TopicKind Kind { get; set; }
    public string TopicId { get; set; } = null!;
    public InterestSource Source { get; set; } = InterestSource.Manual;
}
