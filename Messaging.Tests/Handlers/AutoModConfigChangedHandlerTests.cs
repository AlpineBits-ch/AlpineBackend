using Guild.Contracts.Bus.Events;
using Messaging.Application.Handler.Messages;
using Messaging.Tests.Helpers;

namespace Messaging.Tests.Handlers;

/// <summary>Covers the cache eviction that lets a guild's auto-moderation change take effect on the
/// next send rather than whenever Messaging's cached copy happens to expire.</summary>
[TestFixture]
public class AutoModConfigChangedHandlerTests
{
    [Test]
    public async Task Handle_EvictsTheCachedConfigOfEveryListedChannel()
    {
        var cache = new FakeDistributedCache();
        cache.SetEntry("automod:config:chan-1", "{\"Enabled\":true}");
        cache.SetEntry("automod:config:chan-2", "{\"Enabled\":true}");
        cache.SetEntry("automod:config:chan-3", "{\"Enabled\":true}");

        await AutoModConfigChangedHandler.Handle(
            new AutoModConfigChanged { GuildId = "guild-1", ChannelIds = ["chan-1", "chan-2"] }, cache);

        Assert.Multiple(() =>
        {
            Assert.That(cache.HasEntry("automod:config:chan-1"), Is.False);
            Assert.That(cache.HasEntry("automod:config:chan-2"), Is.False);
            Assert.That(cache.HasEntry("automod:config:chan-3"), Is.True, "Another guild's channel is not this event's business");
        });
    }

    [Test]
    public void Handle_ChannelWithNothingCached_DoesNotThrow()
    {
        var cache = new FakeDistributedCache();

        Assert.DoesNotThrowAsync(() => AutoModConfigChangedHandler.Handle(
            new AutoModConfigChanged { GuildId = "guild-1", ChannelIds = ["chan-1"] }, cache));
    }
}
