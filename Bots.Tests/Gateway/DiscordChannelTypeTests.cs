using Bots.Application.Gateway;

namespace Bots.Tests.Gateway;

/// <summary>Pins the Guild.Domain.Enums.ChannelType name -> Discord numeric channel type mapping
/// used when building GUILD_CREATE/CHANNEL_CREATE dispatch payloads.</summary>
[TestFixture]
public class DiscordChannelTypeTests
{
    [TestCase("Text", DiscordChannelType.GuildText)]
    [TestCase("Voice", DiscordChannelType.GuildVoice)]
    [TestCase("Announcement", DiscordChannelType.GuildAnnouncement)]
    [TestCase("Thread", DiscordChannelType.PublicThread)]
    [TestCase("Forum", DiscordChannelType.GuildForum)]
    [TestCase("Ticket", DiscordChannelType.GuildText)]
    public void FromEnumName_MapsToExpectedDiscordType(string channelTypeName, int expected)
    {
        Assert.That(DiscordChannelType.FromEnumName(channelTypeName), Is.EqualTo(expected));
    }

    [Test]
    public void FromEnumName_UnknownName_FallsBackToGuildText()
    {
        Assert.That(DiscordChannelType.FromEnumName("SomethingNew"), Is.EqualTo(DiscordChannelType.GuildText));
    }
}
