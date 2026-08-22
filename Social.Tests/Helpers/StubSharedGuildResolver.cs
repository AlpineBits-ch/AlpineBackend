using Social.Api.Dtos.Response;
using Social.Api.Services;

namespace Social.Tests.Helpers;

/// <summary>Answers shared guilds from a fixed set of user-id pairs.</summary>
internal sealed class StubSharedGuildResolver : ISharedGuildResolver
{
    private readonly HashSet<string> _sharing;

    public StubSharedGuildResolver(params string[] userIdsSharingWithEveryone)
    {
        _sharing = userIdsSharingWithEveryone.ToHashSet();
    }

    public Task<IReadOnlyList<MutualServerDto>> SharedGuildsAsync(
        string viewerUserId, string subjectUserId, CancellationToken token = default)
        => Task.FromResult<IReadOnlyList<MutualServerDto>>(
            _sharing.Contains(viewerUserId) ? [new MutualServerDto { GuildId = "gld_stub" }] : []);

    public Task<bool> ShareAnyGuildAsync(string userA, string userB, CancellationToken token = default)
        => Task.FromResult(_sharing.Contains(userA) || _sharing.Contains(userB));

    public Task<IReadOnlySet<string>> ShareAnyGuildAsync(
        string userId, IReadOnlyCollection<string> otherUserIds, CancellationToken token = default)
        => Task.FromResult<IReadOnlySet<string>>(otherUserIds.Where(_sharing.Contains).ToHashSet());
}
