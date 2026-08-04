using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Identity.Application.Templates;

/// <summary>
/// Model for the mail sent to an address that already has an account when someone submits it to
/// <c>POST api/v1/authentication/register</c>.
///
/// <para>It deliberately carries nothing the sender supplied - not the username they tried, not the
/// password, not their IP. The recipient is the account holder, the sender is anonymous and quite
/// possibly hostile, and echoing attacker-chosen text into a mail we send on their behalf turns this
/// into a delivery channel for whatever they want to write.</para>
/// </summary>
public class RegistrationAttemptEmail : PageModel
{
    /// <summary>The account's own display name. Never the name the caller tried to register.</summary>
    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public void OnGet()
    {
    }
}
