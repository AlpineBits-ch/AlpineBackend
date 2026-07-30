using Isle.Api.Services.State;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Isle.Tests.Tests.Services;

[TestFixture]
public class CommandCooldownServiceTests
{
    private IDistributedCache _cache = null!;
    private CommandCooldownService _service = null!;

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        _cache = services.BuildServiceProvider().GetRequiredService<IDistributedCache>();
        _service = new CommandCooldownService(_cache);
    }

    [Test]
    public async Task GetRemaining_NoCooldownStarted_ReturnsNull()
    {
        var remaining = await _service.GetRemainingAsync("player-1", "storedino");

        Assert.That(remaining, Is.Null);
    }

    [Test]
    public async Task StartThenGetRemaining_WithinWindow_ReturnsPositiveRemaining()
    {
        await _service.StartAsync("player-1", "storedino", TimeSpan.FromMinutes(5));

        var remaining = await _service.GetRemainingAsync("player-1", "storedino");

        Assert.That(remaining, Is.Not.Null);
        Assert.That(remaining!.Value, Is.LessThanOrEqualTo(TimeSpan.FromMinutes(5)));
        Assert.That(remaining.Value, Is.GreaterThan(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task Start_NonPositiveDuration_IsANoOp()
    {
        await _service.StartAsync("player-1", "storedino", TimeSpan.Zero);

        var remaining = await _service.GetRemainingAsync("player-1", "storedino");

        Assert.That(remaining, Is.Null);
    }

    [Test]
    public async Task Start_NegativeDuration_IsANoOp()
    {
        await _service.StartAsync("player-1", "storedino", TimeSpan.FromMinutes(-1));

        var remaining = await _service.GetRemainingAsync("player-1", "storedino");

        Assert.That(remaining, Is.Null);
    }

    [Test]
    public async Task DifferentCommandsForSamePlayer_AreTrackedIndependently()
    {
        await _service.StartAsync("player-1", "storedino", TimeSpan.FromMinutes(5));

        var otherCommandRemaining = await _service.GetRemainingAsync("player-1", "loaddino");

        Assert.That(otherCommandRemaining, Is.Null);
    }

    [Test]
    public async Task SameCommandForDifferentPlayers_AreTrackedIndependently()
    {
        await _service.StartAsync("player-1", "storedino", TimeSpan.FromMinutes(5));

        var otherPlayerRemaining = await _service.GetRemainingAsync("player-2", "storedino");

        Assert.That(otherPlayerRemaining, Is.Null);
    }

    [Test]
    public async Task GetRemaining_MalformedCacheValue_ReturnsNull()
    {
        await _cache.SetStringAsync("isle:cooldown:storedino:player-1", "not-a-number");

        var remaining = await _service.GetRemainingAsync("player-1", "storedino");

        Assert.That(remaining, Is.Null);
    }
}
