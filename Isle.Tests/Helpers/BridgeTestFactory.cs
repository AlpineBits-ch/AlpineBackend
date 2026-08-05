using IsleBridge.Sdk;
using IsleBridge.Sdk.Models;
using NSubstitute;

namespace Isle.Tests.Helpers;

/// <summary>Canned <see cref="Result"/>s and a ready-to-configure <see cref="IBridgeClient"/> fake.</summary>
internal static class BridgeTestFactory
{
    public static Result Ok() => new() { Ok = true, CodeRaw = "OK" };

    public static Result Fail(string code = "NO_PAWN") => new() { Ok = false, CodeRaw = code };

    /// <summary>
    /// A substitute wired so every action verb succeeds and every read verb returns an empty-ish
    /// snapshot by default - enough for <c>RewardGranter</c> to have "no live pawn" behavior unless a
    /// test overrides a specific call.
    /// </summary>
    public static IBridgeClient CreateDefault()
    {
        var bridge = Substitute.For<IBridgeClient>();

        bridge.SetVitalAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<double>(), Arg.Any<CancellationToken>()).Returns(Ok());
        bridge.SetGrowthAsync(Arg.Any<string>(), Arg.Any<double>(), Arg.Any<CancellationToken>()).Returns(Ok());
        bridge.DmAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<ChatMode>(), Arg.Any<CancellationToken>()).Returns(Ok());
        bridge.GetStatsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<StatsSnapshot>(_ => throw new BridgeCommandException(Fail()));

        return bridge;
    }
}
