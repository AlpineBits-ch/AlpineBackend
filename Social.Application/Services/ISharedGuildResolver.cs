using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Social.Api.Dtos.Response;
using Wolverine;

namespace Social.Api.Services;

/// <summary>
/// "Which guilds do these two people share?" - needed by <c>FriendRequestPolicy.ServerMembers</c>
/// (privacy spec T2-15) and by the <c>mutualServers</c> profile field (T2-17).
///
/// <para>Guild owns the membership table and answers through <see cref="GetSharedGuildsRequest"/>;
/// <see cref="BusSharedGuildResolver"/> is the shipped implementation.
/// <see cref="NoSharedGuildResolver"/> remains as the explicit restrictive stand-in.</para>
///
/// <para>Nothing here applies <c>MutualServersVisibility</c>. That gate lives in
/// <see cref="ProfileProjectionService"/>, deliberately in one place: Guild does not gate on its
/// side precisely so the two cannot double-gate and disagree about which was authoritative.</para>
/// </summary>
public interface ISharedGuildResolver
{
    Task<IReadOnlyList<MutualServerDto>> SharedGuildsAsync(
        string viewerUserId, string subjectUserId, CancellationToken token = default);

    Task<bool> ShareAnyGuildAsync(string userA, string userB, CancellationToken token = default);
}

/// <summary>
/// Resolves shared guilds over the bus from Guild.
///
/// <para><b>Fails closed.</b> A failed call yields "no shared guilds", so a Guild outage makes
/// <c>ServerMembers</c> refuse a friend request and empties <c>mutualServers</c> - it never turns
/// the policy into "allow". That is the whole reason the call is wrapped rather than made inline:
/// an unhandled bus exception at the call site would surface as a 500, and the client retry that
/// followed would eventually land on a healthy pod - a slower, less predictable version of the same
/// refusal, with a spurious error in between.</para>
///
/// <para>No block filtering is re-applied here: Guild applies its own, symmetrically and fail-closed,
/// before answering. Federated shadow guilds are included, because a membership materialized from a
/// remote instance is an ordinary membership - see the note in <see cref="ProfileProjectionService"/>.</para>
/// </summary>
public sealed class BusSharedGuildResolver(IMessageBus bus, ILogger<BusSharedGuildResolver> logger)
    : ISharedGuildResolver
{
    public async Task<IReadOnlyList<MutualServerDto>> SharedGuildsAsync(
        string viewerUserId, string subjectUserId, CancellationToken token = default)
    {
        var guildIds = await SharedGuildIdsAsync(viewerUserId, subjectUserId, token);

        // Name is left null on purpose. It is a display value, Guild does not project it on this
        // contract (it is an authorization input, not a render payload), and a second lookup to
        // fill it in would put another bus round trip on every profile read.
        return guildIds.Select(id => new MutualServerDto { GuildId = id }).ToList();
    }

    public async Task<bool> ShareAnyGuildAsync(string userA, string userB, CancellationToken token = default)
        => (await SharedGuildIdsAsync(userA, userB, token)).Count > 0;

    private async Task<IReadOnlyList<string>> SharedGuildIdsAsync(
        string userId, string otherUserId, CancellationToken token)
    {
        // Guild drops an id equal to UserId rather than answering it - the intersection of a user
        // with themselves is their entire guild list, which is the enumeration that contract
        // refuses to serve. Short-circuiting keeps that from costing a round trip to learn nothing.
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(otherUserId) || userId == otherUserId)
            return [];

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
            return [];
        }

        // A pair with no shared guilds is omitted from Shared rather than returned with an empty
        // list, so a missing entry *is* the answer and needs no separate "not found" branch.
        var summary = response?.Shared?.FirstOrDefault(s => s.OtherUserId == otherUserId);
        return summary?.GuildIds?.ToList() ?? [];
    }
}

/// <summary>
/// The restrictive answer: no shared guilds, ever.
///
/// <para>No longer what <c>Program.cs</c> registers - <see cref="BusSharedGuildResolver"/> is - but
/// kept as the explicit fail-closed stand-in for a deployment or test with no Guild to ask. Never
/// invert it to "true": a resolver that guesses yes would admit every <c>ServerMembers</c> friend
/// request the moment Guild became unreachable, which is exactly the failure the spec's first
/// cross-cutting rule exists to prevent.</para>
/// </summary>
public sealed class NoSharedGuildResolver : ISharedGuildResolver
{
    public Task<IReadOnlyList<MutualServerDto>> SharedGuildsAsync(
        string viewerUserId, string subjectUserId, CancellationToken token = default)
        => Task.FromResult<IReadOnlyList<MutualServerDto>>([]);

    public Task<bool> ShareAnyGuildAsync(string userA, string userB, CancellationToken token = default)
        => Task.FromResult(false);
}
