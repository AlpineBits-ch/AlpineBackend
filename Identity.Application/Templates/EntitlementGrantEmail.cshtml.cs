using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Identity.Application.Templates;

/// <summary>"Somebody changed what your account can do."</summary>
public class EntitlementGrantEmail : PageModel
{
    public string Name { get; set; } = null!;

    public string Email { get; set; } = null!;

    /// <summary>The headline verb, already chosen by the sender so the template does not switch on an
    /// enum it would have to be kept in step with.</summary>
    public string Headline { get; set; } = null!;

    /// <summary>The sentence under the headline.</summary>
    public string Summary { get; set; } = null!;

    /// <summary>The plan's name, when the grant names one.</summary>
    public string? PlanDisplayName { get; set; }

    /// <summary>The specific entitlements, when it names those instead.</summary>
    public List<string> Entitlements { get; set; } = [];

    /// <summary>When it runs out, formatted, or null for a permanent grant and for a revocation.
    /// </summary>
    public string? ExpiresOn { get; set; }

    /// <summary>True when the grant has no end date, which is a materially different thing from "we
    /// do not know when it ends" and has to read as such.</summary>
    public bool IsPermanent { get; set; }
}
