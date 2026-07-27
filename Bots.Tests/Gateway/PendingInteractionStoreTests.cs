using Bots.Application.Gateway;
using Bots.Tests.Helpers;

namespace Bots.Tests.Gateway;

[TestFixture]
public class PendingInteractionStoreTests
{
    [Test]
    public async Task SaveAsync_ThenGetAsync_RoundTrips()
    {
        var store = new PendingInteractionStore(new FakeDistributedCache());
        var interaction = new PendingInteraction("intr_1", "user_bot1", "gild_1", "chan_1", "user_invoker", "ping", Acknowledged: false);

        await store.SaveAsync("token-abc", interaction);
        var loaded = await store.GetAsync("token-abc");

        Assert.That(loaded, Is.EqualTo(interaction));
    }

    [Test]
    public async Task GetAsync_UnknownToken_ReturnsNull()
    {
        var store = new PendingInteractionStore(new FakeDistributedCache());

        var loaded = await store.GetAsync("never-saved");

        Assert.That(loaded, Is.Null);
    }

    [Test]
    public async Task MarkAcknowledgedAsync_UpdatesAcknowledgedFlagWithoutLosingOtherFields()
    {
        var store = new PendingInteractionStore(new FakeDistributedCache());
        var interaction = new PendingInteraction("intr_1", "user_bot1", "gild_1", "chan_1", "user_invoker", "ping", Acknowledged: false);
        await store.SaveAsync("token-abc", interaction);

        await store.MarkAcknowledgedAsync("token-abc", interaction);
        var loaded = await store.GetAsync("token-abc");

        Assert.Multiple(() =>
        {
            Assert.That(loaded!.Acknowledged, Is.True);
            Assert.That(loaded.ChannelId, Is.EqualTo("chan_1"));
            Assert.That(loaded.CommandName, Is.EqualTo("ping"));
        });
    }
}
