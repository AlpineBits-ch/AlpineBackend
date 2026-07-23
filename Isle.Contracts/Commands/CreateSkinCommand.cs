using FluentValidation.Results;
using Isle.Domain.Entity;

namespace Isle.Contracts.Commands;

public class CreateSkinCommand
{
    public CreateSkinParams Parameter { get; set; }
}

public class CreateSkinCommandResponse
{
    public string SkinId { get; set; }
    public ICollection<ValidationFailure> Errors { get; set; } = [];
}