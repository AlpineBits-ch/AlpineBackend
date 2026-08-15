using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Identity.Application.Templates;

/// <summary>"Your plan went up."</summary>
public class PlanUpgradedEmail : PageModel
{
    public string Name { get; set; } = null!;

    public string Email { get; set; } = null!;

    public string PlanDisplayName { get; set; } = null!;

    public string PreviousPlanDisplayName { get; set; } = null!;

    /// <summary>The end of the period that is now paid for, formatted, or null when the subscription
    /// has no period end yet.</summary>
    public string? RenewsOn { get; set; }
}
