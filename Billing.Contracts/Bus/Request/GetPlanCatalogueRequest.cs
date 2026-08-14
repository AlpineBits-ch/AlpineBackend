namespace Billing.Contracts.Bus.Request;

/// <summary>"What plans does this instance have?"</summary>
public class GetPlanCatalogueRequest
{
}

/// <summary>
/// Every plan a grant may name or a subject may be on, plus which of them this instance treats as
/// the default for each kind of subject.
/// </summary>
public class GetPlanCatalogueResponse
{
    public List<CataloguePlanDto> Plans { get; set; } = [];

    /// <summary>The plan an unassigned guild is on, or null when this instance configured none. Null
    /// stays null the whole way to the client: a plan nobody configured must not be invented.
    /// </summary>
    public string? DefaultGuildPlan { get; set; }

    public string? DefaultUserPlan { get; set; }
}

/// <summary>One plan, in the three fields resolution and a settings screen need.</summary>
public class CataloguePlanDto
{
    /// <summary>The lookup key: a bare plan name, or <c>name@number</c> for a specific version.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>What a settings screen calls it.</summary>
    public string? DisplayName { get; set; }

    public Dictionary<string, string> Values { get; set; } = [];
}
