using FluentValidation;

namespace Identity.Domain.Validators;

public class AgeValidator : AbstractValidator<DateOnly>
{
    public AgeValidator()
    {
        RuleFor(x => x).NotEmpty().WithMessage("Age cannot be empty");
        RuleFor(x => x).LessThan(DateOnly.FromDateTime(DateTime.Now.AddYears(-13))).WithMessage("Age must be greater than 13");
    }
}