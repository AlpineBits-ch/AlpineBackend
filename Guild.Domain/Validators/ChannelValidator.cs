using FluentValidation;
using Guild.Domain.Aggregates;
using Guild.Domain.Enums;

namespace Guild.Domain.Validators;

public class ChannelValidator : AbstractValidator<Channel>
{
    public ChannelValidator()
    {
        RuleFor(x => x.Name).NotEmpty();

        // Regular channels are slugs (Discord-style "general", "off-topic") and can't contain
        // spaces, but thread-shaped names are free-text titles (a forum post's title, a discussion
        // thread's subject, a scene called "The Siege of Blackwater") - "Dark mode is too bright"
        // is a completely normal thread name that this rule would otherwise reject.
        RuleFor(x => x.Name)
            .Must(v => !v.Any(char.IsWhiteSpace))
            .WithMessage("Channel name cannot contain whitespace")
            .When(x => !x.Type.IsThreadShaped());

        RuleFor(x => x.Icon)
            .Matches("^[a-z0-9-]{1,48}$")
            .When(x => !string.IsNullOrEmpty(x.Icon))
            .WithMessage("Channel icon must be a lowercase kebab-case name");

        RuleFor(x => x.IconColor)
            .Matches("^#[0-9a-fA-F]{6}$")
            .When(x => !string.IsNullOrEmpty(x.IconColor))
            .WithMessage("Channel icon colour must be #RRGGBB");
    }
}