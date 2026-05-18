using Microsoft.IdentityModel.Tokens;

namespace Identity.Application.Dtos.Response;

public class LoginResponse
{
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public ICollection<ValidationFailure> Failures = new List<ValidationFailure>();
}