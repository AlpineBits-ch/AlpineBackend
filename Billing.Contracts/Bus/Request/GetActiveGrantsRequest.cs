using Echo.Entitlements.Model;

namespace Billing.Contracts.Bus.Request;

/// <summary>"What live grants does this subject hold?"</summary>
public class GetActiveGrantsRequest
{
    public SubjectKind SubjectKind { get; set; }

    public string SubjectId { get; set; } = null!;
}

/// <summary>The grants that are live right now: not revoked, not expired.</summary>
public class GetActiveGrantsResponse
{
    public List<ActiveGrantDto> Grants { get; set; } = [];
}

/// <summary>One grant, in the four fields resolution needs.</summary>
public class ActiveGrantDto
{
    /// <summary>Shown verbatim as provenance, so it has to name something a human can then go and
    /// look up.</summary>
    public string GrantId { get; set; } = null!;

    /// <summary>Set when the grant names a plan.</summary>
    public string? Plan { get; set; }

    public Dictionary<string, string>? Entitlements { get; set; }

    /// <summary>Null means permanent (monetization.md section 6).</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}
