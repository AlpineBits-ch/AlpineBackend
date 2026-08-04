using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Social.Api.Dtos.Response;
using Wolverine;

namespace Social.Api.Services;

/// <summary>
/// The two profile fields Identity owns and Social renders: the subject's birthday and their linked
/// external accounts (privacy spec T2-17).
///
/// <para>Shaped exactly like <see cref="ISharedGuildResolver"/>, and for the same reasons.
/// <see cref="BusIdentityProfileFactsResolver"/> is the shipped implementation;
/// <see cref="NoIdentityProfileFactsResolver"/> is the explicit restrictive stand-in.</para>
///
/// <para><b>Nothing here applies a visibility setting.</b> <c>BirthdayVisibility</c> and
/// <c>ConnectionsVisibility</c> are applied in <see cref="ProfileProjectionService"/>, which is also
/// what decides whether these methods are called at all - a field the viewer may not see is never
/// fetched, so a bug in the projector cannot leak data the service never loaded. Identity applies its
/// own viewer-independent floor on top (it refuses outright when the setting is <c>Nobody</c>), which
/// is strictly weaker and therefore cannot disagree with the gate here.</para>
/// </summary>
public interface IIdentityProfileFactsResolver
{
    Task<DateOnly?> BirthdayAsync(string userId, CancellationToken token = default);

    Task<IReadOnlyList<ProfileConnectionDto>> ConnectionsAsync(string userId, CancellationToken token = default);
}

/// <summary>
/// Resolves both fields over the bus from Identity.
///
/// <para><b>Fails closed.</b> A failed call yields no birthday and no connections, never a guess, and
/// never an exception escaping onto the profile-read path - a 500 on every profile read would be a
/// worse outage than a missing optional field, and the client retry that followed would land on a
/// healthy pod and show the field anyway, just less predictably.</para>
///
/// <para>The contracts are batched (<c>UserIds</c>, not <c>UserId</c>) even though this resolver
/// passes exactly one id: the batching lives in the contract so that a future list projection - a
/// member list, a friend list - is one round trip instead of N, which is the shape that already had
/// to be fixed once in Messaging.</para>
/// </summary>
public sealed class BusIdentityProfileFactsResolver(IMessageBus bus, ILogger<BusIdentityProfileFactsResolver> logger)
    : IIdentityProfileFactsResolver
{
    public async Task<DateOnly?> BirthdayAsync(string userId, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;

        GetUserBirthdaysResponse? response;
        try
        {
            response = await bus.InvokeAsync<GetUserBirthdaysResponse>(
                new GetUserBirthdaysRequest { UserIds = [userId] }, token);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Birthday lookup failed for {UserId}; omitting the field", userId);
            return null;
        }

        return response?.Birthdays?.FirstOrDefault(b => b.UserId == userId)?.BirthDate;
    }

    public async Task<IReadOnlyList<ProfileConnectionDto>> ConnectionsAsync(
        string userId, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return [];

        GetUserConnectionsResponse? response;
        try
        {
            response = await bus.InvokeAsync<GetUserConnectionsResponse>(
                new GetUserConnectionsRequest { UserIds = [userId] }, token);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Connections lookup failed for {UserId}; omitting the field", userId);
            return [];
        }

        var summary = response?.Users?.FirstOrDefault(u => u.UserId == userId);
        if (summary?.Connections is not { Count: > 0 }) return [];

        return summary.Connections
            .Where(c => !string.IsNullOrWhiteSpace(c.Type) && !string.IsNullOrWhiteSpace(c.ExternalId))
            .Select(c => new ProfileConnectionDto
            {
                Type = c.Type,
                ExternalId = c.ExternalId,
                DisplayName = c.DisplayName,
                // Every link type this instance can report was established by an authenticated flow
                // with the provider (Steam OpenID), so there is no unverified variety to distinguish
                // yet. The field stays because a self-asserted link type - a URL a user types in -
                // is the obvious second kind, and it must not arrive looking like this one.
                Verified = true,
            })
            .ToList();
    }
}

/// <summary>
/// The restrictive answer: no birthday, no connections, ever.
///
/// <para>Not what <c>Program.cs</c> registers - <see cref="BusIdentityProfileFactsResolver"/> is -
/// but kept as the explicit fail-closed stand-in for a deployment or test with no Identity to ask.
/// </para>
/// </summary>
public sealed class NoIdentityProfileFactsResolver : IIdentityProfileFactsResolver
{
    public Task<DateOnly?> BirthdayAsync(string userId, CancellationToken token = default)
        => Task.FromResult<DateOnly?>(null);

    public Task<IReadOnlyList<ProfileConnectionDto>> ConnectionsAsync(string userId, CancellationToken token = default)
        => Task.FromResult<IReadOnlyList<ProfileConnectionDto>>([]);
}
