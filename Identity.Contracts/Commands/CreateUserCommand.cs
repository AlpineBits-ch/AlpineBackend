using FluentValidation.Results;

namespace Identity.Contracts.Commands;

public class CreateUserCommand
{
    /// <summary>Specified by the fk user id from our auth provider</summary>
    public string Id { get; set; }
    public string Email { get; set; }
    public DateOnly BirthDate { get; set; }
    public string Username { get; set; }
    public string Password { get; set; }

    /// <summary>Address the registration came from, recorded on the Terms/Privacy consents this
    /// command writes (T1-10). See <c>CreateUserWithEmailAndPasswordRequest.IpAddress</c>.</summary>
    public string? IpAddress { get; set; }
}

public class CreateUserResponse
{
    public string? UserId { get; set; }

    /// <summary>The address already belongs to an account, so nothing was created.</summary>
    public bool EmailAlreadyExists { get; set; }

    public ICollection<ValidationFailure> Errors { get; set; } = new List<ValidationFailure>();
}