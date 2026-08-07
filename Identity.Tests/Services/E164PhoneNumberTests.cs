using Identity.Application.Services;

namespace Identity.Tests.Services;

/// <summary>
/// The only check standing between a user's typing and a number a housemate will copy into a
/// banking app.
/// </summary>
[TestFixture]
public class E164PhoneNumberTests
{
    // ── normal ──────────────────────────────────────────────────────────────

    [Test]
    public void AWellFormedNumberIsReturnedUnchanged()
    {
        Assert.That(E164PhoneNumber.Normalize("+41791234567"), Is.EqualTo("+41791234567"));
    }

    [TestCase("+41 79 123 45 67")]
    [TestCase("+41-79-123-45-67")]
    [TestCase("+41.79.123.45.67")]
    [TestCase("+(41) 79 123 45 67")]
    [TestCase("  +41791234567  ")]
    public void PresentationCharactersAreStripped(string typed)
    {
        // A user pasting from a contact card carries these, and refusing the paste teaches them to
        // retype the number by hand - which is the step where a digit actually gets lost.
        Assert.That(E164PhoneNumber.Normalize(typed), Is.EqualTo("+41791234567"));
    }

    [Test]
    public void ANonBreakingSpaceIsTreatedAsASeparator()
    {
        // Contact cards and web pages are full of U+00A0 and a user has no way of seeing one.
        Assert.That(E164PhoneNumber.Normalize("+41\u00A079\u00A0123\u00A045\u00A067"),
            Is.EqualTo("+41791234567"));
    }

    // ── edge ────────────────────────────────────────────────────────────────

    [Test]
    public void TheShortestAndLongestPermittedLengthsAreAccepted()
    {
        var shortest = "+" + new string('1', E164PhoneNumber.MinDigits);
        var longest = "+" + new string('1', E164PhoneNumber.MaxDigits);

        Assert.Multiple(() =>
        {
            Assert.That(E164PhoneNumber.Normalize(shortest), Is.EqualTo(shortest));
            Assert.That(E164PhoneNumber.Normalize(longest), Is.EqualTo(longest));
        });
    }

    [Test]
    public void OneDigitPastE164IsRejected()
    {
        // E.164 caps the digits at fifteen.
        Assert.That(E164PhoneNumber.Normalize("+" + new string('1', E164PhoneNumber.MaxDigits + 1)),
            Is.Null);
    }

    [Test]
    public void ANumberThatIsAllSeparatorsIsRejected()
    {
        // Stripping happens before the length check, so this is the case where a string that looked
        // long enough to be a number turns out to contain no digits at all.
        Assert.That(E164PhoneNumber.Normalize("+ - . ( )"), Is.Null);
    }

    // ── negative ────────────────────────────────────────────────────────────

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void NothingIsNotANumber(string? typed)
    {
        Assert.That(E164PhoneNumber.Normalize(typed), Is.Null);
    }

    [TestCase("0041791234567", TestName = "Normalize_TrunkPrefixedInternationalDialling_Rejected")]
    [TestCase("0791234567", TestName = "Normalize_NationalFormat_Rejected")]
    public void ANumberWithoutAPlusIsRejected(string typed)
    {
        // "00" means "+" in most of the world and does not in enough of it that rewriting it would
        // silently produce a valid-looking number belonging to somebody else.
        Assert.That(E164PhoneNumber.Normalize(typed), Is.Null);
    }

    [TestCase("+0791234567", TestName = "Normalize_LeadingZeroAfterPlus_Rejected")]
    [TestCase("+ 0 79 123 45 67", TestName = "Normalize_LeadingZeroAfterStripping_Rejected")]
    public void ALeadingZeroIsRejected(string typed)
    {
        // No country code begins with zero, so this is a national trunk prefix that E.164 does not
        // carry - the "079..." a Swiss user would dial at home, with a plus bolted on the front.
        Assert.That(E164PhoneNumber.Normalize(typed), Is.Null);
    }

    [TestCase("+41791234567x89")]
    [TestCase("+4179123456\uFF17")]
    [TestCase("+41 79 CALL ME")]
    [TestCase("+41791234567,,123")]
    public void AnUnexpectedCharacterIsARefusalNotSomethingToSkipPast(string typed)
    {
        // The fullwidth digit is the interesting one: dropping it would leave a ten-digit number
        // that looks entirely reasonable and belongs to somebody else.
        Assert.That(E164PhoneNumber.Normalize(typed), Is.Null);
    }

    [TestCase("+")]
    [TestCase("+1")]
    [TestCase("+4179")]
    public void SomethingTooShortToBeANumberIsRejected(string typed)
    {
        Assert.That(E164PhoneNumber.Normalize(typed), Is.Null);
    }

    // ── masking, which is what reaches the audit table ──────────────────────

    [Test]
    public void MaskKeepsEnoughToRecogniseAndNotEnoughToDial()
    {
        var masked = E164PhoneNumber.Mask("+41791234567");

        Assert.Multiple(() =>
        {
            Assert.That(masked, Is.EqualTo("+41***67"));
            Assert.That(masked, Does.Not.Contain("791234"),
                "IdentityAuditEvent is append-only and never tidied - a full number written there "
                + "is a second copy of the account's most re-identifying field, kept forever");
        });
    }

    [Test]
    public void MaskHandlesAbsenceAndSomethingTooShortToMask()
    {
        Assert.Multiple(() =>
        {
            Assert.That(E164PhoneNumber.Mask(null), Is.EqualTo("(none)"));
            Assert.That(E164PhoneNumber.Mask(""), Is.EqualTo("(none)"));
            // Shorter than prefix + suffix, so there is no room to reveal anything at all.
            Assert.That(E164PhoneNumber.Mask("+1234"), Is.EqualTo("*****"));
        });
    }
}
