using Bots.Domain.Entity;

namespace Bots.Tests.Domain;

[TestFixture]
public class BotInstallationTests
{
    [Test]
    public void Prefix_IsBins()
    {
        Assert.That(BotInstallation.Prefix, Is.EqualTo("bins"));
    }

    [Test]
    public void GenerateId_UsesPrefixedKsuid()
    {
        var id = BotInstallation.GenerateId();

        Assert.That(id, Does.StartWith("bins_"));
    }
}
