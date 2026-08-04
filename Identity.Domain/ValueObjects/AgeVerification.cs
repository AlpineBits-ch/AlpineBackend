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
    ///
    /// <para>Null is not "young". Callers that decide a restriction from this must say what they do
    /// with an unknown age explicitly - see <c>MinorPrivacyFloors</c>, which treats it as
    /// not-a-minor because the alternative would apply child protections to every bot account and to
    /// every account whose data has already been erased.</para>
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
    ///
    /// <para>Evaluated from the stored birth date on every call rather than cached onto a column.
    /// That is what makes the birthday rollover of T1-11 work without a sweep: the moment the date
    /// passes, this returns false and the floors stop applying, so a user who ages out has their
    /// settings unlocked rather than silently kept restricted by a stale flag nobody rewrote.</para>
    /// </summary>
    public bool IsMinorAt(DateTimeOffset now, int ageOfMajority)
    {
        var age = AgeInYearsAt(now);
        return age is not null && age < ageOfMajority;
    }

    /// <summary>
    /// Clears every identifying field, for the account purge (T1-9).
    ///
    /// <para>The birth date is personal data in its own right and one of the most re-identifying
    /// fields on the row - it survived the old tombstone entirely, along with the three verification
    /// timestamps, which together said "this person completed government-ID verification on this
    /// date". The level is reset to <see cref="AgeVertificationLevel.None"/> rather than left as
    /// evidence of what was once verified; whether the deployment retains the one non-identifying
    /// bit it may need for legal defence is <c>ApplicationUser.WasVerifiedAdult</c>'s job, not this
    /// object's.</para>
    /// </summary>
    public void Purge()
    {
        BirthDate = default;
        Level = AgeVertificationLevel.None;
        SelfDeclarationCompletedAt = null;
        AiEstimationCompletedAt = null;
        GovermentIdCompletedAt = null;
    }
}