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

    /// <summary>
    /// Whole years elapsed since <see cref="BirthDate"/>, or null when no birth date was ever
    /// recorded (a bot account, or an account whose age data has been purged - both leave the
    /// default <c>DateOnly</c>).
    /// </summary>
    public int? AgeInYearsAt(DateTimeOffset now)
    {
        if (BirthDate == default) return null;

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (BirthDate > today) return null;

        var age = today.Year - BirthDate.Year;
        // The birthday has not come round yet this year.
        if (BirthDate.AddYears(age) > today) age--;
        return age;
    }

    /// <summary>
    /// Whether the account is below <paramref name="ageOfMajority"/> at <paramref name="now"/>.
    /// </summary>
    public bool IsMinorAt(DateTimeOffset now, int ageOfMajority)
    {
        var age = AgeInYearsAt(now);
        return age is not null && age < ageOfMajority;
    }

    /// <summary>Clears every identifying field, for the account purge (T1-9).</summary>
    public void Purge()
    {
        BirthDate = default;
        Level = AgeVertificationLevel.None;
        SelfDeclarationCompletedAt = null;
        AiEstimationCompletedAt = null;
        GovermentIdCompletedAt = null;
    }
}