using Bots.Application.Gateway;

namespace Bots.Tests.Helpers;

/// <summary>Test double for <see cref="IBotChannelVisibility"/>.</summary>
internal sealed class FakeBotChannelVisibility : IBotChannelVisibility
{
    private readonly Dictionary<string, List<string>> _allowedByChannel = new();

    /// <summary>Channel ids the filter was asked about, in call order.</summary>
    public List<string> QueriedChannelIds { get; } = new();

    /// <summary>Restricts <paramref name="channelId"/> to exactly <paramref name="botUserIds"/>.</summary>
    public FakeBotChannelVisibility Allow(string channelId, params string[] botUserIds)
    {
        _allowedByChannel[channelId] = botUserIds.ToList();
        return this;
    }

    /// <summary>Makes <paramref name="channelId"/> invisible to every bot.</summary>
    public FakeBotChannelVisibility DenyAll(string channelId) => Allow(channelId);

    public Task<IReadOnlyList<string>> FilterToVisibleAsync(string channelId, List<string> botUserIds)
    {
        QueriedChannelIds.Add(channelId);

        IReadOnlyList<string> result = _allowedByChannel.TryGetValue(channelId, out var allowed)
            ? botUserIds.Where(allowed.Contains).ToList()
            : botUserIds;

        return Task.FromResult(result);
    }
}
