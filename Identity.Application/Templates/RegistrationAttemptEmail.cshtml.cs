using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Identity.Application.Templates;

/// <summary>
/// Model for the mail sent to an address that already has an account when someone submits it to
/// <c>POST api/v1/authentication/register</c>.
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
