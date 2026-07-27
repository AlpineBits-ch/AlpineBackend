using Bots.Domain.Entity;

namespace Bots.Tests.Domain;

[TestFixture]
public class BotCommandTests
{
    [Test]
    public void Prefix_IsBoco()
    {
        Assert.That(BotCommand.Prefix, Is.EqualTo("boco"));
    }

    [Test]
    public void GenerateId_UsesPrefixedKsuid()
    {
        var id = BotCommand.GenerateId();

        Assert.That(id, Does.StartWith("boco_"));
    }

    [Test]
    public void GenerateId_ProducesUniqueIdsAcrossCalls()
    {
        var id1 = BotCommand.GenerateId();
        var id2 = BotCommand.GenerateId();

        Assert.That(id1, Is.Not.EqualTo(id2));
    }

    [Test]
    public void OptionsJson_DefaultsToEmptyArray()
    {
        var command = new BotCommand
        {
            Id = BotCommand.GenerateId(),
            BotApplicationId = "boap_1",
            Name = "ping",
            Description = "Replies with pong",
        };

        Assert.That(command.OptionsJson, Is.EqualTo("[]"));
    }

    [Test]
    public void GuildId_DefaultsToNull_MeaningGlobalScope()
    {
        var command = new BotCommand
        {
            Id = BotCommand.GenerateId(),
            BotApplicationId = "boap_1",
            Name = "ping",
            Description = "Replies with pong",
        };

        Assert.That(command.GuildId, Is.Null);
    }
}
