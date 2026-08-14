using Echo.Entitlements.Model;

namespace Billing.Contracts.Bus.Request;

/// <summary>"What version are this subject's entitlements at?"</summary>
public class GetEntitlementVersionRequest
{
    public SubjectKind SubjectKind { get; set; }

    public string SubjectId { get; set; } = null!;
}

/// <summary>Zero means nothing has ever changed this subject's entitlements, which is the honest
/// answer for the overwhelming majority of subjects and is what
/// <c>StaticEntitlementVersionProvider</c> returns before Billing is deployed.</summary>
public class GetEntitlementVersionResponse
{
    public long Version { get; set; }
}
