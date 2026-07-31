using Echo.Realtime.Devices;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Messaging.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Messaging.Tests.Devices;

/// <summary>
/// The shared X-Device-Id read that Messaging's voice/Cloudflare controllers and Guild's voice
/// controller all go through. Three copies of this used to sit inline in those controllers with no
/// validation at all, so a client sending an id that matched no registered device silently got the
/// broken pre-device behaviour back.
/// </summary>
[TestFixture]
public class DeviceIdResolverTests
{
    private const string UserId = "user-1";

    private static HttpRequest RequestWith(string? deviceId)
    {
        var context = new DefaultHttpContext();
        if (deviceId is not null) context.Request.Headers[DeviceIdentity.HeaderName] = deviceId;
        return context.Request;
    }

    private static (DeviceIdResolver Resolver, FakeMessageBus Bus, FakeDistributedCache Cache) Build(bool isRegistered)
    {
        var cache = new FakeDistributedCache();
        var bus = new FakeMessageBus(msg => msg switch
        {
            ValidateUserDeviceRequest => new ValidateUserDeviceResponse { IsRegistered = isRegistered },
            _ => throw new InvalidOperationException("unexpected: " + msg.GetType().Name),
        });
        return (new DeviceIdResolver(bus, cache, NullLogger<DeviceIdResolver>.Instance), bus, cache);
    }

    [Test]
    public async Task NoHeader_FallsBackToDefaultBucket_AndIsNotTreatedAsUnknown()
    {
        // Pre-update builds send nothing. They must keep working (with the old single-device
        // behaviour) rather than start failing.
        var (resolver, bus, _) = Build(isRegistered: false);

        var result = await resolver.ResolveAsync(RequestWith(null), UserId);

        Assert.Multiple(() =>
        {
            Assert.That(result.DeviceId, Is.EqualTo(DeviceIdentity.DefaultDeviceId));
            Assert.That(result.WasProvided, Is.False);
            Assert.That(result.IsUnknown, Is.False);
            Assert.That(bus.Invoked, Is.Empty, "an absent header should not cost a validation round trip");
        });
    }

    [Test]
    public async Task RegisteredDevice_IsAccepted()
    {
        var (resolver, _, _) = Build(isRegistered: true);

        var result = await resolver.ResolveAsync(RequestWith("desktop-1"), UserId);

        Assert.Multiple(() =>
        {
            Assert.That(result.DeviceId, Is.EqualTo("desktop-1"));
            Assert.That(result.IsRegistered, Is.True);
            Assert.That(result.IsUnknown, Is.False);
        });
    }

    [Test]
    public async Task UnregisteredDevice_IsFlaggedUnknown()
    {
        var (resolver, _, _) = Build(isRegistered: false);

        var result = await resolver.ResolveAsync(RequestWith("not-mine"), UserId);

        Assert.Multiple(() =>
        {
            Assert.That(result.WasProvided, Is.True);
            Assert.That(result.IsRegistered, Is.False);
            Assert.That(result.IsUnknown, Is.True);
        });
    }

    [Test]
    public async Task RepeatedResolve_HitsIdentityOnceForAKnownDevice()
    {
        var (resolver, bus, _) = Build(isRegistered: true);

        await resolver.ResolveAsync(RequestWith("desktop-1"), UserId);
        await resolver.ResolveAsync(RequestWith("desktop-1"), UserId);

        Assert.That(bus.Invoked.Count(m => m is ValidateUserDeviceRequest), Is.EqualTo(1));
    }

    [Test]
    public async Task UnregisteredAnswer_IsNotCached()
    {
        // Registering a device and immediately placing a call is a normal first-run sequence; a
        // cached "no" would break it for the length of the TTL.
        var registered = false;
        var cache = new FakeDistributedCache();
        var bus = new FakeMessageBus(_ => new ValidateUserDeviceResponse { IsRegistered = registered });
        var resolver = new DeviceIdResolver(bus, cache, NullLogger<DeviceIdResolver>.Instance);

        var first = await resolver.ResolveAsync(RequestWith("desktop-1"), UserId);
        registered = true;
        var second = await resolver.ResolveAsync(RequestWith("desktop-1"), UserId);

        Assert.Multiple(() =>
        {
            Assert.That(first.IsUnknown, Is.True);
            Assert.That(second.IsUnknown, Is.False);
        });
    }

    [Test]
    public async Task IdentityUnreachable_AcceptsRatherThanBlockingTheCall()
    {
        // Degrading to the old unvalidated behaviour beats taking calls and voice down with
        // Identity.
        var cache = new FakeDistributedCache();
        var bus = new FakeMessageBus(_ => throw new InvalidOperationException("identity down"));
        var resolver = new DeviceIdResolver(bus, cache, NullLogger<DeviceIdResolver>.Instance);

        var result = await resolver.ResolveAsync(RequestWith("desktop-1"), UserId);

        Assert.That(result.IsUnknown, Is.False);
    }
}
