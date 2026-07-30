using Isle.Api.Services.Hosted;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Isle.Tests.Tests.Services.Hosted;

/// <summary>
/// <see cref="QuestDirectorService"/> is a thin dispatcher: every tick it unconditionally opens a DI
/// scope and delegates straight to <c>QuestCompletionService</c>, <c>BountyService</c>,
/// <c>QuestDirector</c> and <c>QuestSpawner</c> — each already has its own dedicated test fixture
/// (QuestCompletionServiceTests, BountyServiceTests, QuestDirectorTests) covering the real decision
/// logic. Standing up that whole graph again here (RewardGranter, QuestAnnouncer, IMessageBus,
/// ISkinStore, IBridgeClient, RegionMap, ...) would just re-test already-covered logic through an extra
/// layer of indirection, so per the "delegates to already-tested logic" strategy this file sticks to a
/// thin smoke test of the wrapper's own lifecycle/scheduling.
/// </summary>
[TestFixture]
public class QuestDirectorServiceTests
{
    [Test]
    public async Task ExecuteAsync_StartThenImmediateStop_NeverCreatesAScopeDuringStartupDelay()
    {
        // StartupDelay is a real 45s, so a quick start/stop only ever exercises that delay's
        // cancellation path and never reaches a tick (SafeTickAsync unconditionally opens a scope).
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var service = new QuestDirectorService(scopeFactory, NullLogger<QuestDirectorService>.Instance);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        cts.Cancel();
        Assert.DoesNotThrowAsync(() => service.StopAsync(CancellationToken.None));

        scopeFactory.DidNotReceive().CreateScope();
    }

    [Test]
    public void Constructor_DoesNotThrow()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        Assert.DoesNotThrow(() => new QuestDirectorService(scopeFactory, NullLogger<QuestDirectorService>.Instance));
    }
}
