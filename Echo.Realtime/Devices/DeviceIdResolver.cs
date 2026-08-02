using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Echo.Realtime.Devices;

/// <summary>The outcome of reading <c>X-Device-Id</c> off a request.</summary>
/// <param name="DeviceId">The id to key call/voice state on.</param>
/// <param name="WasProvided">
/// False when the client sent no header at all (a pre-update build).
/// </param>
/// <param name="IsRegistered">True only when the id matches an active device of this user.</param>
public readonly record struct DeviceIdResult(string DeviceId, bool WasProvided, bool IsRegistered)
{
    /// <summary>A header was sent, but it names no device this user owns.</summary>
    public bool IsUnknown => WasProvided && !IsRegistered;
}

/// <summary>Shared constants for the client device id.</summary>
public static class DeviceIdentity
{
    public const string HeaderName = "X-Device-Id";
    public const string QueryName = "deviceId";

    /// <summary>Bucket used when a client sends no device id, so pre-update builds keep working
    /// (with the old single-device behaviour) instead of failing outright.</summary>
    public const string DefaultDeviceId = "default";
}

/// <summary>Resolves and validates the caller's device id.</summary>
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

    /// <summary>
    /// The caller's device id only if it really is one of this user's registered devices, and null
    /// otherwise - including when Identity cannot be reached.
    /// </summary>
    public async Task<string?> ResolveVerifiedAsync(
        HttpRequest request, string userId, CancellationToken ct = default)
    {
        var header = request.Headers[DeviceIdentity.HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(header)) return null;

        var deviceId = header.Trim();
        return await IsRegisteredAsync(userId, deviceId, ct, failOpen: false) ? deviceId : null;
    }

    /// <summary>Whether the id names an active device of this user.</summary>
    public async Task<bool> IsRegisteredAsync(
        string userId, string deviceId, CancellationToken ct = default, bool failOpen = true)
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
            // Identity being unreachable must not take calls and voice down with it.
            logger.LogWarning(ex,
                "Device validation unavailable for user {UserId}, device {DeviceId} treated as {Verdict}",
                userId, deviceId, failOpen ? "valid" : "unverified");
            return failOpen;
        }

        if (!response.IsRegistered) return false;

        await cache.SetStringAsync(key, "1", CacheOptions, ct);
        return true;
    }
}
