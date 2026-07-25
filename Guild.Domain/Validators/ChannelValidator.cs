using FluentValidation;
using Guild.Domain.Aggregates;

namespace Guild.Domain.Validators;

public class ChannelValidator : AbstractValidator<Channel>
{
    public ChannelValidator()
    {
        RuleFor(x => x.Name).NotEmpty()
            .Must(v => !v.Any(Char.IsWhiteSpace))
            .WithMessage("Channel name cannot contain whitespace");
    }
}