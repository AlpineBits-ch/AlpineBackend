using FluentValidation;
using Guild.Domain.Entity;

namespace Guild.Domain.Validators;

public class SceneFolderValidator : AbstractValidator<SceneFolder>
{
    public SceneFolderValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(SceneFolder.MaxNameLength);

        RuleFor(x => x.Color)
            .Matches("^#[0-9A-Fa-f]{6}$")
            .When(x => x.Color is not null)
            .WithMessage("Color must be a hex string in #RRGGBB form");

        // A folder that parents itself is a rail that never terminates.
        RuleFor(x => x)
            .Must(x => x.ParentFolderId != x.Id)
            .WithName(nameof(SceneFolder.ParentFolderId))
            .WithMessage("A folder cannot be its own parent");
    }
}
