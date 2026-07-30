using Isle.Api.Services.Hosted;
using Isle.Api.Services.Rcon;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Isle.Tests.Tests.Services.Hosted;

/// <summary>
/// <see cref="RconConnectionService"/>'s loop calls <c>EnsureConnectedAsync</c> BEFORE delaying on each
/// iteration (unlike most of its siblings, which delay first), so a short-lived
/// <c>CancellationTokenSource</c> that's still open when the service starts is enough to observe at
/// least one real keepalive call before cancellation stops the loop — no need to touch a private tick
/// method here.
/// </summary>
[TestFixture]
public class RconConnectionServiceTests
{
    [Test]
    public async Task ExecuteAsync_RunsBriefly_CallsEnsureConnectedAtLeastOnceBeforeCancellation()
    {
        var rcon = Substitute.For<IRconGateway>();
        var service = new RconConnectionService(rcon, NullLogger<RconConnectionService>.Instance);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(50); // give the loop a chance to run its first iteration
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        await rcon.Received().EnsureConnectedAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_PreCancelledToken_NeverCallsEnsureConnected()
    {
        var rcon = Substitute.For<IRconGateway>();
        var service = new RconConnectionService(rcon, NullLogger<RconConnectionService>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await service.StartAsync(cts.Token);
        await service.StopAsync(CancellationToken.None);

        await rcon.DidNotReceive().EnsureConnectedAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_EnsureConnectedThrows_KeepsLoopingInsteadOfCrashing()
    {
        var rcon = Substitute.For<IRconGateway>();
        rcon.EnsureConnectedAsync(Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(new InvalidOperationException("unreachable"));
        var service = new RconConnectionService(rcon, NullLogger<RconConnectionService>.Instance);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await Task.Delay(50);
        cts.Cancel();

        Assert.DoesNotThrowAsync(() => service.StopAsync(CancellationToken.None));
    }
}
