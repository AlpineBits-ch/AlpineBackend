using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Identity.Application.Templates;

public class PasswordResetEmail : PageModel
{
    public string Name { get; set; }
    public string Email { get; set; }
    public string ResetCode { get; set; }
    public void OnGet()
    {

    }
}
