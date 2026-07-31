using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Echo.Realtime.Devices;

/// <summary>
/// The outcome of reading <c>X-Device-Id</c> off a request.
/// </summary>
/// <param name="DeviceId">The id to key call/voice state on. Always non-empty - falls back to
/// <see cref="DeviceIdentity.DefaultDeviceId"/> when the caller sent nothing.</param>
/// <param name="WasProvided">False when the client sent no header at all (a pre-update build).</param>
/// <param name="IsRegistered">True only when the id matches an active device of this user.</param>
public readonly record struct DeviceIdResult(string DeviceId, bool WasProvided, bool IsRegistered)
{
    /// <summary>A header was sent, but it names no device this user owns. The caller should
    /// reject: honouring it would silently key call/voice state on an id nothing else will ever
    /// match, which is exactly the split-brain multi-device behaviour the device id exists to
    /// prevent.</summary>
    public bool IsUnknown => WasProvided && !IsRegistered;
}

/// <summary>
/// Shared constants for the client device id. The value is the ClientDeviceId a client registers
/// with Identity for MLS; it travels as the <c>X-Device-Id</c> header on REST calls and as the
/// <c>deviceId</c> query parameter on the realtime hub connection.
/// </summary>
public static class DeviceIdentity
{
    public const string HeaderName = "X-Device-Id";
    public const string QueryName = "deviceId";

    /// <summary>Bucket used when a client sends no device id, so pre-update builds keep working
    /// (with the old single-device behaviour) instead of failing outright.</summary>
    public const string DefaultDeviceId = "default";
}

/// <summary>
/// Resolves and validates the caller's device id. Three services read this header (Messaging calls,
/// Messaging Cloudflare, Guild voice) and each used to re-implement the read inline with no check
/// at all, so a client sending an unstable or wrong id silently degraded to the pre-device
/// behaviour with nothing server-side noticing.
///
/// <para>Validation asks Identity over the bus and caches positive answers briefly - a device is
/// registered once and then used on every call action, so the uncached path is rare. Negative
/// answers are deliberately not cached: registering a device and immediately placing a call is a
/// normal first-run sequence, and a cached "no" would break it for the length of the TTL.</para>
/// </summary>
public sealed class DeviceIdResolver(IMessageBus bus, IDistributedCache cache, ILogger<DeviceIdResolver> logger)
{
    /// <summary>Short enough that a removed device stops being accepted promptly, long enough that
    /// a burst of call/voice actions costs one bus round trip rather than one each.</summary>
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
    };

    private static string CacheKey(string userId, string deviceId) => $"device-valid:{userId}:{deviceId}";

    public async Task<DeviceIdResult> ResolveAsync(HttpRequest request, string userId, CancellationToken ct = default)
    {
        var header = request.Headers[DeviceIdentity.HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return new DeviceIdResult(DeviceIdentity.DefaultDeviceId, WasProvided: false, IsRegistered: false);
        }

        var deviceId = header.Trim();
        return new DeviceIdResult(deviceId, WasProvided: true, await IsRegisteredAsync(userId, deviceId, ct));
    }

    private async Task<bool> IsRegisteredAsync(string userId, string deviceId, CancellationToken ct)
    {
        var key = CacheKey(userId, deviceId);
        if (await cache.GetStringAsync(key, ct) is not null) return true;

        ValidateUserDeviceResponse response;
        try
        {
            response = await bus.InvokeAsync<ValidateUserDeviceResponse>(
                new ValidateUserDeviceRequest { UserId = userId, ClientDeviceId = deviceId }, ct);
        }
        catch (Exception ex)
        {
            // Identity being unreachable must not take calls and voice down with it. Trust the id
            // for this request and log it - the failure mode of guessing "valid" here is the old
            // unvalidated behaviour, which is strictly better than a hard outage.
            logger.LogWarning(ex, "Device validation unavailable for user {UserId}, accepting device {DeviceId} unverified",
                userId, deviceId);
            return true;
        }

        if (!response.IsRegistered) return false;

        await cache.SetStringAsync(key, "1", CacheOptions, ct);
        return true;
    }
}
