using Identity.Domain.ValueObjects;
using Identity.Domain.Validators;

namespace Identity.Tests.Domain;

[TestFixture]
public class EmailValidatorTests
{
    private EmailValidator _validator = null!;

    [SetUp]
    public void SetUp() => _validator = new EmailValidator();

    [Test]
    public void Validate_WellFormedEmail_Passes()
    {
        var result = _validator.Validate(new Email("racer@example.com"));

        Assert.That(result.IsValid, Is.True);
    }

    [TestCase("")]
    [TestCase("not-an-email")]
    [TestCase("missing-at-sign.com")]
    public void Validate_MalformedEmail_Fails(string value)
    {
        var result = _validator.Validate(new EmailWithoutCtorValidation(value));

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void Validate_DisposableEmailDomain_FailsWithSpecificErrorCode()
    {
        // mailinator.com is a well-known disposable-email provider.
        var result = _validator.Validate(new EmailWithoutCtorValidation("someone@mailinator.com"));

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors.Select(e => e.ErrorCode), Does.Contain("EmailDisposableNotAllowed"));
    }

    /// <summary>Email's own constructor already validates-and-throws, so to exercise the
    /// validator against a deliberately invalid value we need an unvalidated instance -
    /// this local subclass swaps that out for a plain property set.</summary>
    private sealed class EmailWithoutCtorValidation : Email
    {
        public EmailWithoutCtorValidation(string value) : base("placeholder@example.com")
        {
            Value = value;
        }
    }
}
