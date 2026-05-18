using FluentValidation;
using Identity.Domain.Enums;
using Identity.Domain.Validators;

namespace Identity.Domain.ValueObjects;

public class AgeVerification
{
    public DateTimeOffset? SelfDeclarationCompletedAt { get; set; }
    public DateTimeOffset? AiEstimationCompletedAt { get; set; }
    public DateTimeOffset? GovermentIdCompletedAt { get; set; }
    public DateOnly BirthDate { get; set; }
    
    public AgeVertificationLevel Level { get; set; } = AgeVertificationLevel.None;

    public static AgeVerification CreateInitial(DateOnly birthdate)
    {
        var ageValidator = new AgeValidator();
        ageValidator.ValidateAndThrow(birthdate);
        return new AgeVerification
        {
            BirthDate = birthdate,
            Level = AgeVertificationLevel.SelfDeclaration,
            SelfDeclarationCompletedAt = DateTimeOffset.UtcNow
        };
    }
}