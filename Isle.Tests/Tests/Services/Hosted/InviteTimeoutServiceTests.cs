using Isle.Api.Services.Hosted;
using Isle.Domain.Aggregates;
using Isle.Tests.Helpers;
using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Isle.Tests.Tests.Services.Hosted;

/// <summary>
/// <see cref="InviteTimeoutService"/>'s loop delays for <c>Interval</c> (20s) before every tick, so
/// a short-lived <c>StartAsync</c>/<c>StopAsync</c> lifecycle test would never actually run
/// <c>ExpireStaleInvitesAsync</c> - it would only ever exercise the cancellation path.
/// </summary>
[TestFixture]
public class InviteTimeoutServiceTests
{
    private TestIsleContext _context = null!;
    private IBridgeClient _bridge = null!;
    private IServiceScopeFactory _scopeFactory = null!;
    private InviteTimeoutService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _context = TestIsleContext.Create();
        _bridge = Substitute.For<IBridgeClient>();

        var services = new ServiceCollection();
        services.AddSingleton<Isle.Infrastructure.Persistence.MicroserviceContext>(_context);
        var provider = services.BuildServiceProvider();
        _scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        _service = new InviteTimeoutService(_scopeFactory, _bridge, NullLogger<InviteTimeoutService>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        _service.Dispose();
        await _context.DisposeAsync();
    }

    private static PlayerInvite StaleInvite(Player sender, Player receiver)
    {
        var invite = PlayerInvite.Create(sender.Id, receiver.Id);
        invite.CreatedAt = DateTimeOffset.UtcNow - PlayerInvite.Timeout - TimeSpan.FromSeconds(5);
        return invite;
    }

    [Test]
    public async Task ExpireStaleInvitesAsync_PendingInvitePastTimeout_MarksExpiredAndNotifiesSender()
    {
        var sender = TestData.Player("steam-sender", inGameName: "Sender");
        var receiver = TestData.Player("steam-receiver", inGameName: "Receiver");
        _context.Players.AddRange(sender, receiver);
        var invite = StaleInvite(sender, receiver);
        _context.PlayerInvites.Add(invite);
        await _context.SaveChangesAsync();

        await _service.ExpireStaleInvitesAsync(CancellationToken.None);

        var reloaded = await _context.PlayerInvites.FindAsync(invite.Id);
        Assert.That(reloaded!.Status, Is.EqualTo(PlayerInviteStatus.Expired));
        await _bridge.Received(1).DmAsync(
            Arg.Is<string>(s => s.Contains("timed out")),
            steam: sender.SteamId,
            sender: "VENTA.GG",
            mode: ChatMode.Spatial);
    }

    [Test]
    public async Task ExpireStaleInvitesAsync_PendingInviteNotYetTimedOut_LeavesItPendingAndDoesNotNotify()
    {
        var sender = TestData.Player("steam-sender");
        var receiver = TestData.Player("steam-receiver");
        _context.Players.AddRange(sender, receiver);
        var invite = PlayerInvite.Create(sender.Id, receiver.Id); // fresh, well inside the timeout window
        _context.PlayerInvites.Add(invite);
        await _context.SaveChangesAsync();

        await _service.ExpireStaleInvitesAsync(CancellationToken.None);

        var reloaded = await _context.PlayerInvites.FindAsync(invite.Id);
        Assert.That(reloaded!.Status, Is.EqualTo(PlayerInviteStatus.Pending));
        await _bridge.DidNotReceive().DmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ChatMode>());
    }

    [Test]
    public async Task ExpireStaleInvitesAsync_AlreadyAcceptedInvitePastWindow_IsNotTouched()
    {
        var sender = TestData.Player("steam-sender");
        var receiver = TestData.Player("steam-receiver");
        _context.Players.AddRange(sender, receiver);
        var invite = StaleInvite(sender, receiver);
        invite.Accept();
        _context.PlayerInvites.Add(invite);
        await _context.SaveChangesAsync();

        await _service.ExpireStaleInvitesAsync(CancellationToken.None);

        var reloaded = await _context.PlayerInvites.FindAsync(invite.Id);
        Assert.That(reloaded!.Status, Is.EqualTo(PlayerInviteStatus.Accepted));
    }

    [Test]
    public async Task ExpireStaleInvitesAsync_NoStaleInvites_NoOp()
    {
        await _service.ExpireStaleInvitesAsync(CancellationToken.None);

        await _bridge.DidNotReceiveWithAnyArgs().DmAsync(default!);
    }

    [Test]
    public async Task ExpireStaleInvitesAsync_NotifyThrows_StillExpiresAndDoesNotPropagate()
    {
        var sender = TestData.Player("steam-sender");
        var receiver = TestData.Player("steam-receiver");
        _context.Players.AddRange(sender, receiver);
        var invite = StaleInvite(sender, receiver);
        _context.PlayerInvites.Add(invite);
        await _context.SaveChangesAsync();

        _bridge.DmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ChatMode>())
            .ThrowsAsync(new InvalidOperationException("bridge unreachable"));

        Assert.DoesNotThrowAsync(() => _service.ExpireStaleInvitesAsync(CancellationToken.None));

        var reloaded = await _context.PlayerInvites.FindAsync(invite.Id);
        Assert.That(reloaded!.Status, Is.EqualTo(PlayerInviteStatus.Expired));
    }

    [Test]
    public async Task ExecuteAsync_StartThenImmediateStop_CompletesWithoutRunningATick()
    {
        // Smoke test for the hosted wrapper: Interval is a real 20s, so a quick start/stop only ever
        // exercises the Task.Delay cancellation path, never ExpireStaleInvitesAsync (covered directly above).
        using var cts = new CancellationTokenSource();
        await _service.StartAsync(cts.Token);
        cts.Cancel();
        Assert.DoesNotThrowAsync(() => _service.StopAsync(CancellationToken.None));
    }
}
