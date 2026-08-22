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

    /// <summary>
    /// Which of <paramref name="otherUserIds"/> share a guild with <paramref name="userId"/>, in
    /// one round trip. The canvas fan-out asks this once for a whole friend list.
    /// </summary>
    Task<IReadOnlySet<string>> ShareAnyGuildAsync(
        string userId, IReadOnlyCollection<string> otherUserIds, CancellationToken token = default);
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

    public async Task<IReadOnlySet<string>> ShareAnyGuildAsync(
        string userId, IReadOnlyCollection<string> otherUserIds, CancellationToken token = default)
    {
        var others = otherUserIds.Where(id => !string.IsNullOrWhiteSpace(id) && id != userId).Distinct().ToList();
        if (string.IsNullOrWhiteSpace(userId) || others.Count == 0) return new HashSet<string>();

        GetSharedGuildsResponse? response;
        try
        {
            response = await bus.InvokeAsync<GetSharedGuildsResponse>(
                new GetSharedGuildsRequest { UserId = userId, OtherUserIds = others }, token);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Batch shared-guild lookup failed for {UserId}; treating none as shared", userId);
            return new HashSet<string>();
        }

        // A pair with no shared guilds is omitted from Shared, so the returned set is the answer.
        return response?.Shared?.Select(s => s.OtherUserId).ToHashSet() ?? new HashSet<string>();
    }

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

    public Task<IReadOnlySet<string>> ShareAnyGuildAsync(
        string userId, IReadOnlyCollection<string> otherUserIds, CancellationToken token = default)
        => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
}
