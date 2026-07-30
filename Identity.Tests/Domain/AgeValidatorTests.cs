using Identity.Domain.Validators;

namespace Identity.Tests.Domain;

[TestFixture]
public class AgeValidatorTests
{
    private AgeValidator _validator = null!;

    [SetUp]
    public void SetUp() => _validator = new AgeValidator();

    [Test]
    public void Validate_DefaultDateOnly_FailsNotEmptyRule()
    {
        var result = _validator.Validate(default(DateOnly));

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void Validate_ExactlyThirteenYearsAgo_Fails()
    {
        // RuleFor(...).LessThan(Now.AddYears(-13)) - exactly 13 years ago is not strictly less than
        // the cutoff, so it must be rejected (the rule requires the user to be OLDER than 13).
        var exactlyThirteen = DateOnly.FromDateTime(DateTime.Now.AddYears(-13));

        var result = _validator.Validate(exactlyThirteen);

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void Validate_FourteenYearsAgo_Passes()
    {
        var fourteenYearsAgo = DateOnly.FromDateTime(DateTime.Now.AddYears(-14));

        var result = _validator.Validate(fourteenYearsAgo);

        Assert.That(result.IsValid, Is.True);
    }

    [Test]
    public void Validate_TenYearsAgo_Fails()
    {
        var tenYearsAgo = DateOnly.FromDateTime(DateTime.Now.AddYears(-10));

        var result = _validator.Validate(tenYearsAgo);

        Assert.That(result.IsValid, Is.False);
    }
}
