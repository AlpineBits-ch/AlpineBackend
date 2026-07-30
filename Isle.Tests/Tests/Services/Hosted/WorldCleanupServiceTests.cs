using Isle.Api.Services;
using Isle.Api.Services.Hosted;
using Isle.Api.Services.Rcon;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TheIsleEvrimaRconClient;
using TheIsleEvrimaRconClient.Extensions.Models;

namespace Isle.Tests.Tests.Services.Hosted;

/// <summary>
/// <see cref="WorldCleanupService"/>'s loop schedules against real wall-clock hours (up to an hour
/// between actions), so it's driven here through its `internal` pieces instead: the pure scheduling math
/// (<c>NextFullHour</c>, <c>DelayUntil</c>) and the RCON-backed steps (<c>IsBelowPopulationGateAsync</c>,
/// <c>SafeAnnounceAsync</c>, <c>SafeCleanupAsync</c>), plus a smoke test of the wrapper's own loop exit.
/// </summary>
[TestFixture]
public class WorldCleanupServiceTests
{
    private IRconGateway _rcon = null!;
    private WorldCleaner _cleaner = null!;
    private WorldCleanupService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _rcon = Substitute.For<IRconGateway>();
        _cleaner = new WorldCleaner(_rcon, NullLogger<WorldCleaner>.Instance);
        _service = new WorldCleanupService(_rcon, _cleaner, NullLogger<WorldCleanupService>.Instance);
    }

    [TearDown]
    public void TearDown() => _service.Dispose();

    // ---- NextFullHour ---------------------------------------------------------------------

    [Test]
    public void NextFullHour_MidHour_RoundsUpToTheNextHourBoundary()
    {
        var now = new DateTimeOffset(2026, 7, 30, 14, 23, 10, TimeSpan.Zero);

        var next = WorldCleanupService.NextFullHour(now);

        Assert.That(next, Is.EqualTo(new DateTimeOffset(2026, 7, 30, 15, 0, 0, TimeSpan.Zero)));
    }

    [Test]
    public void NextFullHour_ExactlyOnTheHour_StillAdvancesToTheFollowingHour()
    {
        var now = new DateTimeOffset(2026, 7, 30, 14, 0, 0, TimeSpan.Zero);

        var next = WorldCleanupService.NextFullHour(now);

        Assert.That(next, Is.EqualTo(new DateTimeOffset(2026, 7, 30, 15, 0, 0, TimeSpan.Zero)));
    }

    [Test]
    public void NextFullHour_JustBeforeMidnight_RollsOverToTheNextDay()
    {
        var now = new DateTimeOffset(2026, 7, 30, 23, 59, 59, TimeSpan.Zero);

        var next = WorldCleanupService.NextFullHour(now);

        Assert.That(next, Is.EqualTo(new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero)));
    }

    // ---- DelayUntil -------------------------------------------------------------------------

    [Test]
    public async Task DelayUntil_TargetAlreadyPast_ReturnsTrueImmediately()
    {
        var result = await WorldCleanupService.DelayUntil(DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task DelayUntil_PreCancelledToken_ReturnsFalse()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await WorldCleanupService.DelayUntil(DateTimeOffset.UtcNow.AddMinutes(5), cts.Token);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task DelayUntil_ShortFutureTarget_WaitsThenReturnsTrue()
    {
        var result = await WorldCleanupService.DelayUntil(DateTimeOffset.UtcNow.AddMilliseconds(20), CancellationToken.None);

        Assert.That(result, Is.True);
    }

    // ---- IsBelowPopulationGateAsync ----------------------------------------------------------

    [Test]
    public async Task IsBelowPopulationGateAsync_PopulationBelowGate_ReturnsTrue()
    {
        _rcon.ExecuteAsync(Arg.Any<Func<EvrimaRconClient, Task<ServerDetails>>>())
            .Returns(Task.FromResult(new ServerDetails { CurrentPlayers = 5 }));

        Assert.That(await _service.IsBelowPopulationGateAsync(), Is.True);
    }

    [Test]
    public async Task IsBelowPopulationGateAsync_PopulationAtGate_ReturnsFalse()
    {
        _rcon.ExecuteAsync(Arg.Any<Func<EvrimaRconClient, Task<ServerDetails>>>())
            .Returns(Task.FromResult(new ServerDetails { CurrentPlayers = 40 }));

        Assert.That(await _service.IsBelowPopulationGateAsync(), Is.False);
    }

    [Test]
    public async Task IsBelowPopulationGateAsync_RconFails_FallsBackToRunningTheCleanup()
    {
        _rcon.ExecuteAsync(Arg.Any<Func<EvrimaRconClient, Task<ServerDetails>>>())
            .ThrowsAsync(new TimeoutException("rcon unreachable"));

        Assert.That(await _service.IsBelowPopulationGateAsync(), Is.True);
    }

    // ---- SafeAnnounceAsync / SafeCleanupAsync ------------------------------------------------

    [Test]
    public async Task SafeAnnounceAsync_HappyPath_SendsOneAnnouncement()
    {
        await _service.SafeAnnounceAsync();

        await _rcon.Received(1).ExecuteAsync(Arg.Any<Func<EvrimaRconClient, Task>>());
    }

    [Test]
    public async Task SafeAnnounceAsync_RconThrows_SwallowsTheException()
    {
        _rcon.ExecuteAsync(Arg.Any<Func<EvrimaRconClient, Task>>()).ThrowsAsync(new InvalidOperationException("down"));

        Assert.DoesNotThrowAsync(() => _service.SafeAnnounceAsync());
    }

    [Test]
    public async Task SafeCleanupAsync_HappyPath_WipesCorpsesAndTogglesAiOffThenOn()
    {
        await _service.SafeCleanupAsync();

        await _rcon.Received(3).ExecuteAsync(Arg.Any<Func<EvrimaRconClient, Task>>());
    }

    [Test]
    public async Task SafeCleanupAsync_CleanerThrows_SwallowsTheException()
    {
        _rcon.ExecuteAsync(Arg.Any<Func<EvrimaRconClient, Task>>()).ThrowsAsync(new InvalidOperationException("down"));

        Assert.DoesNotThrowAsync(() => _service.SafeCleanupAsync());
    }

    // ---- Hosted wrapper smoke test ----------------------------------------------------------

    [Test]
    public async Task ExecuteAsync_PreCancelledToken_ExitsTheLoopWithoutTouchingRcon()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await _service.StartAsync(cts.Token);
        Assert.DoesNotThrowAsync(() => _service.StopAsync(CancellationToken.None));

        await _rcon.DidNotReceiveWithAnyArgs().ExecuteAsync(Arg.Any<Func<EvrimaRconClient, Task>>());
    }
}
