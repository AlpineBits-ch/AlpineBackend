using Identity.Domain.Aggregates;
using Identity.Domain.Enums;

namespace User.Tests;

/// <summary>Covers the wire translation for <see cref="UserInterests"/>.</summary>
public class UserInterestsTests
{
    [Test]
    public void ToWire_None_IsEmpty()
    {
        Assert.That(UserInterests.None.ToWire(), Is.Empty);
    }

    [Test]
    public void ToWire_SingleFlag_NamesJustThatOne()
    {
        Assert.That(UserInterests.Isle.ToWire(), Is.EqualTo(new[] { "isle" }));
        Assert.That(UserInterests.Social.ToWire(), Is.EqualTo(new[] { "social" }));
    }

    [Test]
    public void ToWire_BothFlags_NamesBoth()
    {
        Assert.That((UserInterests.Isle | UserInterests.Social).ToWire(),
            Is.EqualTo(new[] { "isle", "social" }));
    }

    [Test]
    public void TryParseWire_KnownNames_RoundTrip()
    {
        Assert.That(UserInterestsExtensions.TryParseWire(new[] { "isle", "social" }, out var both), Is.True);
        Assert.That(both, Is.EqualTo(UserInterests.Isle | UserInterests.Social));
        Assert.That(both.ToWire(), Is.EqualTo(new[] { "isle", "social" }));
    }

    [Test]
    public void TryParseWire_IsCaseAndWhitespaceTolerant()
    {
        Assert.That(UserInterestsExtensions.TryParseWire(new[] { " Isle ", "SOCIAL" }, out var parsed), Is.True);
        Assert.That(parsed, Is.EqualTo(UserInterests.Isle | UserInterests.Social));
    }

    [Test]
    public void TryParseWire_Duplicates_AreIdempotent()
    {
        Assert.That(UserInterestsExtensions.TryParseWire(new[] { "isle", "isle" }, out var parsed), Is.True);
        Assert.That(parsed, Is.EqualTo(UserInterests.Isle));
    }

    /// <summary>
    /// An empty answer is refused rather than stored, because "wants neither half" is a state the
    /// client cannot render: it cannot decide whether the account owes a master key, so it would
    /// either ask forever or never ask at all.
    /// </summary>
    [Test]
    public void TryParseWire_NullOrEmpty_IsRefused()
    {
        Assert.That(UserInterestsExtensions.TryParseWire(null, out var fromNull), Is.False);
        Assert.That(fromNull, Is.EqualTo(UserInterests.None));

        Assert.That(UserInterestsExtensions.TryParseWire(Array.Empty<string>(), out var fromEmpty), Is.False);
        Assert.That(fromEmpty, Is.EqualTo(UserInterests.None));
    }

    /// <summary>Refused whole, not partially accepted.</summary>
    [Test]
    public void TryParseWire_UnknownName_RefusesTheWholeSet()
    {
        Assert.That(UserInterestsExtensions.TryParseWire(new[] { "isle", "dinosaurs" }, out var parsed), Is.False);
        Assert.That(parsed, Is.EqualTo(UserInterests.None));
    }

    /// <summary>
    /// Nothing signs into a bot account interactively, so a null onboarding stamp would be a gate
    /// with nobody on the far side of it.
    /// </summary>
    [Test]
    public void CreateBot_IsAlreadyOnboarded()
    {
        var bot = ApplicationUser.CreateBot("user_bot1", "Test Bot");

        Assert.That(bot.OnboardedAt, Is.Not.Null);
        Assert.That(bot.Interests, Is.EqualTo(UserInterests.Social));
    }

    /// <summary>
    /// The mirror image: a human account must start un-onboarded, because that null is the entire
    /// trigger for the picker.
    /// </summary>
    [Test]
    public void Create_StartsUnOnboarded()
    {
        var user = ApplicationUser.Create(new CreateUserParams
        {
            Email = "someone@example.com",
            PhoneNumber = "+41000000000",
            Username = "someone",
            BirthDate = new DateOnly(2000, 1, 1),
        });

        Assert.That(user.OnboardedAt, Is.Null);
        Assert.That(user.Interests, Is.EqualTo(UserInterests.None));
    }
}
