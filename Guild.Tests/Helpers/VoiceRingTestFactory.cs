using Echo.Realtime;
using Echo.Realtime.Caching;
using Echo.Voice.Rooms;
using Echo.Voice.Testing;
using Guild.Application.Services;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine;

namespace Guild.Tests.Helpers;

/// <summary>A clock a test can move.</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset UtcNowValue { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => UtcNowValue;

    public void Advance(TimeSpan by) => UtcNowValue = UtcNowValue.Add(by);
}

/// <summary>Wires a real <see cref="VoiceRingService"/> over a caller's own fakes.</summary>
public static class VoiceRingTestFactory
{
    public static VoiceRingService Create(
        MicroserviceContext context,
        IDistributedCache cache,
        IDistributedLockService locks,
        IHubContext<EchoRealtimeHub> hub,
        IMessageBus bus,
        VoiceRingStore? store = null,
        VoiceRingThrottle? throttle = null,
        TimeProvider? clock = null)
    {
        var permissions = new GuildPermissionService(cache, context, NullLogger<GuildPermissionService>.Instance);

        var service = new VoiceRingService(
            context,
            permissions,
            VoiceTestHarness.StoreFor(cache, locks),
            store ?? new VoiceRingStore(locks, cache),
            throttle ?? new VoiceRingThrottle(cache),
            new BlockCache(cache, bus, NullLogger<BlockCache>.Instance),
            new NotificationResolutionService(context),
            hub,
            bus,
            NullLogger<VoiceRingService>.Instance);

        if (clock is not null) service.Clock = clock;
        return service;
    }
}
