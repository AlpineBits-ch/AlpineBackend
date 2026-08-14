using Echo.Entitlements.Model;
using Persistence;

namespace Billing.Domain.Aggregates;

/// <summary>
/// A per-subject counter that goes up whenever anything changes what that subject is entitled to.
/// </summary>
public class EntitlementVersion : BaseEntity<EntitlementVersion>, IPrefixedEntity
{
    public static string Prefix { get; } = "entv";

    public SubjectKind SubjectKind { get; set; }

    public string SubjectId { get; set; } = null!;

    public long Version { get; set; }
}
