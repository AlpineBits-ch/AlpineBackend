using FluentValidation.Results;

namespace Isle.Contracts.Commands;

public class CreatePlayerCommand
{
    public string SteamId { get; set; }
    public string? UserId { get; set; }
    public bool IsAdmin { get; set; }
}

public class CreatePlayerCommandResponse
{
    public string PlayerId { get; set; }
    public ICollection<ValidationFailure> Failures { get; set; } = [];
}