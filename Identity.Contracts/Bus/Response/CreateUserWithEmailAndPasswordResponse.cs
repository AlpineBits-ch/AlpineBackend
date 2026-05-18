using FluentValidation.Results;

namespace Identity.Contracts.Bus.Response;

public class CreateUserWithEmailAndPasswordResponse
{
    public string UserId { get; set; }

    public ICollection<ValidationFailure> Failures { get; set; }= [];
}