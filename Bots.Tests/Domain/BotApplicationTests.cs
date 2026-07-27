using Bots.Domain.Entity;

namespace Bots.Tests.Domain;

[TestFixture]
public class BotApplicationTests
{
    [Test]
    public void Prefix_IsBoap()
    {
        Assert.That(BotApplication.Prefix, Is.EqualTo("boap"));
    }

    [Test]
    public void GenerateId_UsesPrefixedKsuid()
    {
        var id = BotApplication.GenerateId();

        Assert.That(id, Does.StartWith("boap_"));
    }

    [Test]
    public void GenerateId_ProducesUniqueIdsAcrossCalls()
    {
        var id1 = BotApplication.GenerateId();
        var id2 = BotApplication.GenerateId();

        Assert.That(id1, Is.Not.EqualTo(id2));
    }

    [Test]
    public void IsEnabled_DefaultsToTrue()
    {
        var app = new BotApplication
        {
            Id = BotApplication.GenerateId(),
            OwnerUserId = "user_owner",
            BotUserId = "user_bot",
            Name = "Test Bot",
        };

        Assert.That(app.IsEnabled, Is.True);
    }
}
