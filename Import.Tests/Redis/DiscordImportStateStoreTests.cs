using Import.Application.Redis;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Import.Tests.Redis;

[TestFixture]
public class DiscordImportStateStoreTests
{
    private IDistributedCache _cache = null!;
    private DiscordImportStateStore _store = null!;

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        _cache = services.BuildServiceProvider().GetRequiredService<IDistributedCache>();
        _store = new DiscordImportStateStore(_cache);
    }

    [Test]
    public async Task ConsumeAsync_AfterSave_ReturnsTheSavedUserId()
    {
        await _store.SaveAsync("state-1", "usr_abc");

        var result = await _store.ConsumeAsync("state-1");

        Assert.That(result, Is.EqualTo("usr_abc"));
    }

    [Test]
    public async Task ConsumeAsync_CalledTwice_SecondCallReturnsNull()
    {
        await _store.SaveAsync("state-1", "usr_abc");

        await _store.ConsumeAsync("state-1");
        var second = await _store.ConsumeAsync("state-1");

        Assert.That(second, Is.Null, "state must be consumed exactly once");
    }

    [Test]
    public async Task ConsumeAsync_UnknownState_ReturnsNull()
    {
        var result = await _store.ConsumeAsync("never-saved");

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task SaveAsync_DifferentStates_AreIndependentlyTracked()
    {
        await _store.SaveAsync("state-a", "usr_a");
        await _store.SaveAsync("state-b", "usr_b");

        Assert.That(await _store.ConsumeAsync("state-a"), Is.EqualTo("usr_a"));
        Assert.That(await _store.ConsumeAsync("state-b"), Is.EqualTo("usr_b"));
    }
}
