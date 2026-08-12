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

    private const string ReturnUrl = "venta://discord-import";

    [Test]
    public async Task ConsumeAsync_AfterSave_ReturnsTheSavedUserId()
    {
        await _store.SaveAsync("state-1", "usr_abc", ReturnUrl);

        var result = await _store.ConsumeAsync("state-1");

        Assert.That(result!.RequestingUserId, Is.EqualTo("usr_abc"));
    }

    /// <summary>
    /// The return target has to survive the round trip, because it is the only copy: it is deliberately
    /// not carried in the OAuth <c>state</c> Discord echoes back, where it would be attacker-settable and
    /// would make the callback an open redirect with a valid state token.
    /// </summary>
    [Test]
    public async Task ConsumeAsync_AfterSave_ReturnsTheSavedReturnUrl()
    {
        await _store.SaveAsync("state-1", "usr_abc", "https://app.venta.gg/discord-import");

        var result = await _store.ConsumeAsync("state-1");

        Assert.That(result!.ReturnUrl, Is.EqualTo("https://app.venta.gg/discord-import"));
    }

    /// <summary>
    /// A token written by the build before the return URL existed has no newline in it.
    /// </summary>
    [Test]
    public async Task ConsumeAsync_StateWrittenWithoutAReturnUrl_FallsBackToTheDefault()
    {
        await _cache.SetStringAsync("import-state:legacy", "usr_abc");

        var result = await _store.ConsumeAsync("legacy");

        Assert.That(result!.RequestingUserId, Is.EqualTo("usr_abc"));
        Assert.That(result.ReturnUrl,
            Is.EqualTo(Import.Application.Endpoints.DiscordImportReturnTargets.Default));
    }

    [Test]
    public async Task ConsumeAsync_CalledTwice_SecondCallReturnsNull()
    {
        await _store.SaveAsync("state-1", "usr_abc", ReturnUrl);

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
        await _store.SaveAsync("state-a", "usr_a", ReturnUrl);
        await _store.SaveAsync("state-b", "usr_b", ReturnUrl);

        Assert.That((await _store.ConsumeAsync("state-a"))!.RequestingUserId, Is.EqualTo("usr_a"));
        Assert.That((await _store.ConsumeAsync("state-b"))!.RequestingUserId, Is.EqualTo("usr_b"));
    }
}
