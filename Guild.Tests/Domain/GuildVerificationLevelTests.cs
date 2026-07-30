using Guild.Domain.Enums;

namespace Guild.Tests.Domain;

[TestFixture]
public class GuildVerificationLevelTests
{
    [Test]
    public void None_AlwaysMeetsRequirement_RegardlessOfAccountState()
    {
        var result = GuildVerificationLevel.None.MeetsRequirement(emailConfirmed: false, accountAge: TimeSpan.Zero);
        Assert.That(result, Is.True);
    }

    [TestCase(true, ExpectedResult = true)]
    [TestCase(false, ExpectedResult = false)]
    public bool Low_RequiresOnlyEmailConfirmed(bool emailConfirmed) =>
        GuildVerificationLevel.Low.MeetsRequirement(emailConfirmed, accountAge: TimeSpan.Zero);

    [Test]
    public void Medium_EmailConfirmedButAccountTooNew_Fails()
    {
        var result = GuildVerificationLevel.Medium.MeetsRequirement(emailConfirmed: true, accountAge: TimeSpan.FromMinutes(4));
        Assert.That(result, Is.False);
    }

    [Test]
    public void Medium_EmailConfirmedAndAccountOldEnough_Passes()
    {
        var result = GuildVerificationLevel.Medium.MeetsRequirement(emailConfirmed: true, accountAge: TimeSpan.FromMinutes(5));
        Assert.That(result, Is.True);
    }

    [Test]
    public void Medium_AccountOldEnoughButEmailNotConfirmed_Fails()
    {
        var result = GuildVerificationLevel.Medium.MeetsRequirement(emailConfirmed: false, accountAge: TimeSpan.FromMinutes(30));
        Assert.That(result, Is.False);
    }

    [Test]
    public void High_MeetsMediumThreshold_ButNotHigh_Fails()
    {
        var result = GuildVerificationLevel.High.MeetsRequirement(emailConfirmed: true, accountAge: TimeSpan.FromMinutes(7));
        Assert.That(result, Is.False);
    }

    [Test]
    public void High_EmailConfirmedAndAccountOldEnough_Passes()
    {
        var result = GuildVerificationLevel.High.MeetsRequirement(emailConfirmed: true, accountAge: TimeSpan.FromMinutes(10));
        Assert.That(result, Is.True);
    }

    [Test]
    public void High_ExactlyAtBoundary_Passes()
    {
        // >= not >, so exactly 10 minutes must pass, not just fail-at-the-edge.
        var result = GuildVerificationLevel.High.MeetsRequirement(emailConfirmed: true, accountAge: TimeSpan.FromMinutes(10));
        Assert.That(result, Is.True);
    }
}
