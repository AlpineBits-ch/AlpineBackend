using Isle.Api.Services.Hosted;
using Isle.Api.Services.Quests;
using Isle.Api.Services.World;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Isle.Tests.Tests.Services.Hosted;

/// <summary>
/// <see cref="QuestProgressService"/>'s guard clauses (stale/never-populated roster; roster
/// unchanged since the last credit) are cheap to exercise directly — they never touch the DI scope
/// at all — via the `internal` <c>SafeTickAsync</c>.
/// </summary>
[TestFixture]
public class QuestProgressServiceTests
{
    private WorldRosterCache _roster = null!;

    [SetUp]
    public void SetUp() => _roster = new WorldRosterCache();

    private QuestProgressService BuildService(IServiceScopeFactory scopeFactory) =>
        new(scopeFactory, _roster, NullLogger<QuestProgressService>.Instance);

    [Test]
    public async Task SafeTickAsync_RosterNeverPopulated_NeverCreatesAScope()
    {
        // LastUpdatedAt is null -> IsStale is always true.
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var service = BuildService(scopeFactory);

        await service.SafeTickAsync(CancellationToken.None);

        scopeFactory.DidNotReceive().CreateScope();
    }

    [Test]
    public async Task SafeTickAsync_FreshNewRoster_AttemptsATick()
    {
        _roster.Replace([]); // fresh (just updated) and never credited before -> should attempt a tick

        // QuestCompletionService isn't registered in this scope, so resolving it throws — caught
        // and logged by SafeTickAsync's own try/catch.
        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(QuestCompletionService)).Returns((object?)null);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        var service = BuildService(scopeFactory);

        Assert.DoesNotThrowAsync(() => service.SafeTickAsync(CancellationToken.None));
        scopeFactory.Received(1).CreateScope();
    }

    [Test]
    public async Task ExecuteAsync_StartThenImmediateStop_NeverCreatesAScopeDuringStartupDelay()
    {
        // StartupDelay is a real 35s, so a quick start/stop only ever exercises that delay's
        // cancellation path.
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var service = BuildService(scopeFactory);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        cts.Cancel();
        Assert.DoesNotThrowAsync(() => service.StopAsync(CancellationToken.None));

        scopeFactory.DidNotReceive().CreateScope();
    }
}
