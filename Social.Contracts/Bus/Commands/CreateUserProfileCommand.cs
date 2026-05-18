using FluentValidation.Results;

namespace Social.Contracts.Bus.Commands;

public class CreateUserProfileCommand
{
    public string UserId { get; set; }
    public string Username { get; set; }
    public string Bio { get; set; }
}

public class CreateUserProfileCommandResponse
{
    public string UserId { get; set; }
    public string ProfileId { get; set; }
    public ICollection<ValidationFailure> Errors { get; set; } = new List<ValidationFailure>();
}