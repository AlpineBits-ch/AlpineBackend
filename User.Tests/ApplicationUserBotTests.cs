using Identity.Domain.Aggregates;
using Identity.Domain.Enums;

namespace User.Tests;

/// <summary>
/// Covers ApplicationUser.CreateBot - the factory used to provision a bot account.
/// </summary>
public class ApplicationUserBotTests
{
    [Test]
    public void CreateBot_UsesCallerSuppliedId_NotAGeneratedOne()
    {
        var bot = ApplicationUser.CreateBot("user_deadbeef", "Test Bot");

        Assert.That(bot.Id, Is.EqualTo("user_deadbeef"));
    }

    [Test]
    public void CreateBot_SetsUserTypeToBot()
    {
        var bot = ApplicationUser.CreateBot("user_1", "Test Bot");

        Assert.That(bot.UserType, Is.EqualTo(UserType.Bot));
    }

    [Test]
    public void CreateBot_SetsUsernameFromName()
    {
        var bot = ApplicationUser.CreateBot("user_1", "Captain Hook");

        Assert.Multiple(() =>
        {
            Assert.That(bot.UserName, Is.EqualTo("Captain Hook"));
            Assert.That(bot.NormalizedUserName, Is.EqualTo("CAPTAIN HOOK"));
        });
    }

    [Test]
    public void CreateBot_HasNoEmailOrPhoneNumber()
    {
        var bot = ApplicationUser.CreateBot("user_1", "Test Bot");

        Assert.Multiple(() =>
        {
            Assert.That(bot.Email, Is.Null);
            Assert.That(bot.PhoneNumber, Is.Null);
        });
    }

    [Test]
    public void CreateBot_IsSigninAllowed_TrueByDefault()
    {
        var bot = ApplicationUser.CreateBot("user_1", "Test Bot");

        Assert.That(bot.IsSigninAllowed(), Is.True);
    }

    [Test]
    public void CreateBot_PopulatesAgeVerificationAndUserPreferences_SoSaveDoesNotViolateRequiredOwnedEntities()
    {
        // AgeVerification and UserPreferences are required owned/related entities on
        // ApplicationUser (see Identity.Infrastructure.Persistence.MicroserviceContext) -
        // CreateBot must still populate them even though bots skip age verification.
        var bot = ApplicationUser.CreateBot("user_1", "Test Bot");

        Assert.Multiple(() =>
        {
            Assert.That(bot.AgeVerification, Is.Not.Null);
            Assert.That(bot.AgeVerification.Level, Is.EqualTo(AgeVertificationLevel.None));
            Assert.That(bot.UserPreferences, Is.Not.Null);
            Assert.That(bot.UserPreferences.Id, Is.Not.Null.And.Not.Empty);
        });
    }

    [Test]
    public void CreateBot_DoesNotRaiseUserCreatedDomainEvent()
    {
        // Bots must not go through the human "new user" event pipeline (welcome email,
        // registration saga) - see ApplicationUser.CreateBot's doc comment.
        var bot = ApplicationUser.CreateBot("user_1", "Test Bot");

        Assert.That(bot.GetDomainEvents(), Is.Empty);
    }
}
