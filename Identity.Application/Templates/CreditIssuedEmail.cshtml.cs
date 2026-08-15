using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Identity.Application.Templates;

/// <summary>"You have been given credit."</summary>
public class CreditIssuedEmail : PageModel
{
    public string Name { get; set; } = null!;

    public string Email { get; set; } = null!;

    /// <summary>Already formatted with thousands separators, because a Razor template is the wrong
    /// place to decide a culture.</summary>
    public string Points { get; set; } = null!;

    public string BalancePoints { get; set; } = null!;

    /// <summary>The date this parcel lapses, formatted, or null when it does not.</summary>
    public string? ExpiresOn { get; set; }

    /// <summary>Whether it came from a campaign rather than from one member of staff.</summary>
    public bool FromCampaign { get; set; }

    /// <summary>Billing's own <c>CreditDisclaimer.Text</c>, carried on the message.</summary>
    public string Disclaimer { get; set; } = null!;
}
