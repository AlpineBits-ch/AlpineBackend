using FluentValidation;
using Guild.Domain.Entity;

namespace Guild.Domain.Validators;

public class SceneTagValidator : AbstractValidator<SceneTag>
{
    public SceneTagValidator()
    {
        // Free text with spaces allowed ("slow burn"): it is a display label, never a slug.
        RuleFor(x => x.Name).NotEmpty().MaximumLength(SceneTag.MaxNameLength);

        RuleFor(x => x.Color)
            .Matches("^#[0-9A-Fa-f]{6}$")
            .WithMessage("Color must be a hex string in #RRGGBB form");

        RuleFor(x => x)
            .Must(x => x.EmojiId is null || x.EmojiName is null)
            .WithName(nameof(SceneTag.EmojiId))
            .WithMessage("A tag carries at most one emoji, so set EmojiId or EmojiName, not both");
    }
}
