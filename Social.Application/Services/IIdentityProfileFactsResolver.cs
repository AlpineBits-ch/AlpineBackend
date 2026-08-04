using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Social.Api.Dtos.Response;
using Wolverine;

namespace Social.Api.Services;

/// <summary>
/// The two profile fields Identity owns and Social renders: the subject's birthday and their linked
/// external accounts (privacy spec T2-17).
/// </summary>
public interface IIdentityProfileFactsResolver
{
    Task<DateOnly?> BirthdayAsync(string userId, CancellationToken token = default);

    Task<IReadOnlyList<ProfileConnectionDto>> ConnectionsAsync(string userId, CancellationToken token = default);
}

/// <summary>Resolves both fields over the bus from Identity.</summary>
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
                // with the provider (Steam OpenID), so there is no unverified variety to
                // distinguish yet.
                Verified = true,
            })
            .ToList();
    }
}

/// <summary>The restrictive answer: no birthday, no connections, ever.</summary>
public sealed class NoIdentityProfileFactsResolver : IIdentityProfileFactsResolver
{
    public Task<DateOnly?> BirthdayAsync(string userId, CancellationToken token = default)
        => Task.FromResult<DateOnly?>(null);

    public Task<IReadOnlyList<ProfileConnectionDto>> ConnectionsAsync(string userId, CancellationToken token = default)
        => Task.FromResult<IReadOnlyList<ProfileConnectionDto>>([]);
}
