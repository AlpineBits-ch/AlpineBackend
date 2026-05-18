using FluentValidation.Results;

namespace Identity.Contracts.Bus.Response;

public class LoginWithEmailAndPasswordResponse
{
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public ICollection<ValidationFailure> Failures = new List<ValidationFailure>();
}