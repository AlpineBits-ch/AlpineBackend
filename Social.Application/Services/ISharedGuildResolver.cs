using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Social.Api.Dtos.Response;
using Wolverine;

namespace Social.Api.Services;

/// <summary>
/// "Which guilds do these two people share?" - needed by <c>FriendRequestPolicy.ServerMembers</c>
/// (privacy spec T2-15) and by the <c>mutualServers</c> profile field (T2-17).
/// </summary>
public interface ISharedGuildResolver
{
    Task<IReadOnlyList<MutualServerDto>> SharedGuildsAsync(
        string viewerUserId, string subjectUserId, CancellationToken token = default);

    Task<bool> ShareAnyGuildAsync(string userA, string userB, CancellationToken token = default);
}

/// <summary>Resolves shared guilds over the bus from Guild.</summary>
public sealed class BusSharedGuildResolver(IMessageBus bus, ILogger<BusSharedGuildResolver> logger)
    : ISharedGuildResolver
{
    public async Task<IReadOnlyList<MutualServerDto>> SharedGuildsAsync(
        string viewerUserId, string subjectUserId, CancellationToken token = default)
    {
        var summary = await SharedSummaryAsync(viewerUserId, subjectUserId, token);
        if (summary is null) return [];

        // Guilds is the newer of the two fields; an older Guild build answers only GuildIds, and a
        // nameless list still renders as icons.
        if (summary.Guilds.Count > 0)
            return summary.Guilds.Select(g => new MutualServerDto { GuildId = g.Id, Name = g.Name }).ToList();

        return summary.GuildIds.Select(id => new MutualServerDto { GuildId = id }).ToList();
    }

    public async Task<bool> ShareAnyGuildAsync(string userA, string userB, CancellationToken token = default)
        => (await SharedSummaryAsync(userA, userB, token))?.GuildIds.Count > 0;

    private async Task<SharedGuildsSummary?> SharedSummaryAsync(
        string userId, string otherUserId, CancellationToken token)
    {
        // Guild drops an id equal to UserId rather than answering it - the intersection of a user
        // with themselves is their entire guild list, which is the enumeration that contract
        // refuses to serve. Short-circuiting keeps that from costing a round trip to learn nothing.
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(otherUserId) || userId == otherUserId)
            return null;

        GetSharedGuildsResponse? response;
        try
        {
            response = await bus.InvokeAsync<GetSharedGuildsResponse>(
                new GetSharedGuildsRequest { UserId = userId, OtherUserIds = [otherUserId] }, token);
        }
        catch (Exception e)
        {
            logger.LogWarning(e,
                "Shared-guild lookup failed; treating {UserId} and {OtherUserId} as sharing none", userId, otherUserId);
            return null;
        }

        // A pair with no shared guilds is omitted from Shared rather than returned with an empty
        // list, so a missing entry *is* the answer and needs no separate "not found" branch.
        return response?.Shared?.FirstOrDefault(s => s.OtherUserId == otherUserId);
    }
}

/// <summary>The restrictive answer: no shared guilds, ever.</summary>
public sealed class NoSharedGuildResolver : ISharedGuildResolver
{
    public Task<IReadOnlyList<MutualServerDto>> SharedGuildsAsync(
        string viewerUserId, string subjectUserId, CancellationToken token = default)
        => Task.FromResult<IReadOnlyList<MutualServerDto>>([]);

    public Task<bool> ShareAnyGuildAsync(string userA, string userB, CancellationToken token = default)
        => Task.FromResult(false);
}
