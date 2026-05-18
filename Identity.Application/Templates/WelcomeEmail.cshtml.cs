using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Identity.Application.Templates;

public class WelcomeEmail : PageModel
{
    public string Name { get; set; }
    public string Email { get; set; }
    public string ConfirmationCode { get; set; }
    public void OnGet()
    {
        
    }
}