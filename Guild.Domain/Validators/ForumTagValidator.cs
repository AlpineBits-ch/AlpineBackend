using FluentValidation;
using Guild.Domain.Entity;

namespace Guild.Domain.Validators;

public class ForumTagValidator : AbstractValidator<ForumTag>
{
    public ForumTagValidator()
    {
        // Unlike a channel name, a tag name is free text with spaces allowed ("needs repro") -
        // it's a display label, never a slug.
        RuleFor(x => x.Name).NotEmpty().MaximumLength(ForumTag.MaxNameLength);

        RuleFor(x => x.Color)
            .Matches("^#[0-9A-Fa-f]{6}$")
            .WithMessage("Color must be a hex string in #RRGGBB form");

        RuleFor(x => x)
            .Must(x => x.EmojiId is null || x.EmojiName is null)
            .WithName(nameof(ForumTag.EmojiId))
            .WithMessage("A tag carries at most one emoji - set EmojiId or EmojiName, not both");
    }
}
