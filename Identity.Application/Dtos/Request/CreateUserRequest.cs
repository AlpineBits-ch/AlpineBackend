using ChatAIze.PerfectEmail;
using FluentValidation;

namespace Identity.Application.Dtos.Request;

public class CreateUserRequest
{
    public string Email { get; set; }
    public string Password { get; set; }
    public DateTime BirthDate { get; set; }
    
    public string Username { get; set; }
}

public class EmailValidator : AbstractValidator<CreateUserRequest>
{
    public EmailValidator()
    {
        RuleFor(x => x.Email).NotEmpty().WithMessage("Email cannot be empty").WithErrorCode("EmailNotEmpty");
        RuleFor(x => x.Email).EmailAddress().WithMessage("Invalid email format").WithErrorCode("EmailInvalidFormat");

        RuleFor(x => x.Email)
            .Must(email => !DisposableEmailDetector.IsDisposableEmail(email))
            .WithMessage("One-time or disposable email addresses are not allowed.").WithErrorCode("EmailDisposableNotAllowed");
    }
}