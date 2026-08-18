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
    }
}