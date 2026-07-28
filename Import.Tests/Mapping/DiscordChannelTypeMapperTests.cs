using Import.Application.Mapping;

namespace Import.Tests.Mapping;

[TestFixture]
public class DiscordChannelTypeMapperTests
{
    [TestCase(0, "Text")]
    [TestCase(2, "Voice")]
    [TestCase(5, "Announcement")]
    [TestCase(13, "Voice")] // Stage - lossy, no Echo equivalent
    [TestCase(15, "Forum")]
    [TestCase(16, "Forum")] // Media - lossy, closest fit
    [TestCase(999, "Text")] // unknown type - safe default
    public void ToEchoChannelType_MapsExpectedType(int discordType, string expected)
    {
        Assert.That(DiscordChannelTypeMapper.ToEchoChannelType(discordType), Is.EqualTo(expected));
    }

    [Test]
    public void IsCategory_OnlyTrueForType4()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DiscordChannelTypeMapper.IsCategory(4), Is.True);
            Assert.That(DiscordChannelTypeMapper.IsCategory(0), Is.False);
            Assert.That(DiscordChannelTypeMapper.IsCategory(2), Is.False);
        });
    }

    [TestCase(11, true)]  // public thread
    [TestCase(12, true)]  // private thread
    [TestCase(0, false)]
    [TestCase(4, false)]
    public void IsThread_IdentifiesThreadTypes(int discordType, bool expected)
    {
        Assert.That(DiscordChannelTypeMapper.IsThread(discordType), Is.EqualTo(expected));
    }
}
