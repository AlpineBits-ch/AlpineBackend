using FluentValidation.Results;

namespace Identity.Contracts.Bus.Response;

/// <summary>The registration handler's answer to the controller.</summary>
public class CreateUserWithEmailAndPasswordResponse
{
    public ICollection<ValidationFailure> Failures { get; set; } = [];
}
