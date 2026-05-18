using ChatAIze.PerfectEmail;
using FluentValidation;
using Identity.Domain.ValueObjects;

namespace Identity.Domain.Validators;

public class EmailValidator : AbstractValidator<Email>
{
    public EmailValidator()
    {
        RuleFor(x => x.Value).NotEmpty().WithMessage("Email cannot be empty").WithErrorCode("EmailNotEmpty");
        RuleFor(x => x.Value).EmailAddress().WithMessage("Invalid email format").WithErrorCode("EmailInvalidFormat");

        RuleFor(x => x.Value)
            .Must(email => !DisposableEmailDetector.IsDisposableEmail(email))
            .WithMessage("One-time or disposable email addresses are not allowed.").WithErrorCode("EmailDisposableNotAllowed");
    }
}