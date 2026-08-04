using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Api.Services;
using Social.Infrastructure.Persistence;

namespace Social.Tests.Helpers;

/// <summary>
/// Builds a <see cref="PrivacySettingsCache"/> over the existing hand-rolled fakes, and mints the
/// shipped defaults so a test only has to state the one field it cares about.
/// </summary>
internal static class PrivacyTestHelpers
{
    /// <summary>The defaults from docs/specs/privacy.md §1.1 - what a real account has unless the
    /// user changed something. Distinct from <see cref="PrivacySettingsCache.RestrictiveDefaults"/>,
    /// which is what a *failed lookup* produces.</summary>
    public static UserPrivacySettingsSummary Defaults(string userId) => new()
    {
        UserId = userId,
        AllowDataCollection = false,
        AllowPersonalization = false,
        AllowVoiceRecordingInClips = false,
        DirectMessagePolicy = DirectMessagePolicy.Friends,
        FriendRequestPolicy = FriendRequestPolicy.Everyone,
        DiscoverableByUsername = true,
        DiscoverableByEmail = false,
        DiscoverableByPhone = false,
        MutualServersVisibility = Visibility.Friends,
        MutualFriendsVisibility = Visibility.Friends,
        ConnectionsVisibility = Visibility.Friends,
        BirthdayVisibility = Visibility.Nobody,
        ShareActivity = true,
        AllowPositionalVoiceCapture = true,
        SendReadReceipts = true,
        SendTypingIndicators = true,
        ExplicitContentFilter = ExplicitContentFilter.UnknownSenders,
        HidePushContent = false,
        Version = 1,
    };

    /// <summary>A cache whose bus answers with exactly <paramref name="settings"/>.</summary>
    public static PrivacySettingsCache CacheReturning(
        FakeDistributedCache cache, FakeMessageBus bus, params UserPrivacySettingsSummary[] settings)
    {
        bus.RespondWith<GetUserPrivacySettingsRequest, GetUserPrivacySettingsResponse>(
            new GetUserPrivacySettingsResponse { Settings = settings.ToList() });

        return new PrivacySettingsCache(cache, bus, NullLogger<PrivacySettingsCache>.Instance);
    }

    /// <summary>A cache whose bus has no registered answer - <see cref="FakeMessageBus"/> throws,
    /// which is the outage the fail-closed path exists for.</summary>
    public static PrivacySettingsCache FailingCache(FakeDistributedCache cache, FakeMessageBus bus) =>
        new(cache, bus, NullLogger<PrivacySettingsCache>.Instance);

    /// <summary>The real bus-backed resolver over <see cref="FakeMessageBus"/>.</summary>
    public static BusSharedGuildResolver SharedGuilds(FakeMessageBus bus) =>
        new(bus, NullLogger<BusSharedGuildResolver>.Instance);

    /// <summary>The <see cref="UserDirectory"/> chokepoint over a test context and privacy cache -
    /// the only supported way to find a user by something a human typed (T2-16).</summary>
    public static UserDirectory Directory(MicroserviceContext ctx, PrivacySettingsCache privacy) =>
        new(ctx, privacy);

    /// <summary>The real bus-backed Identity resolver over <see cref="FakeMessageBus"/>.</summary>
    public static BusIdentityProfileFactsResolver IdentityFacts(FakeMessageBus bus) =>
        new(bus, NullLogger<BusIdentityProfileFactsResolver>.Instance);

    /// <summary>Registers the batched birthday answer Identity would give (T2-17).</summary>
    public static void RegisterBirthdays(FakeMessageBus bus, params (string UserId, DateOnly? BirthDate)[] birthdays) =>
        bus.RespondWith<GetUserBirthdaysRequest, GetUserBirthdaysResponse>(new GetUserBirthdaysResponse
        {
            Birthdays = birthdays
                .Select(b => new UserBirthdaySummary { UserId = b.UserId, BirthDate = b.BirthDate })
                .ToList(),
        });

    /// <summary>Registers the batched linked-account answer Identity would give (T2-17).</summary>
    public static void RegisterConnections(
        FakeMessageBus bus, string userId, params ExternalConnectionSummary[] connections) =>
        bus.RespondWith<GetUserConnectionsRequest, GetUserConnectionsResponse>(new GetUserConnectionsResponse
        {
            Users = [new UserConnectionsSummary { UserId = userId, Connections = connections.ToList() }],
        });

    /// <summary>One linked Steam account, the only connection type this codebase has.</summary>
    public static ExternalConnectionSummary Steam(string steamId) => new()
    {
        Type = ExternalConnectionTypes.Steam,
        ExternalId = steamId,
    };

    /// <summary>Registers a shared-guild answer on <paramref name="bus"/> and returns the resolver.
    /// Mirrors Guild's stated convention: a user with no shared guilds is <b>omitted</b> from
    /// <c>Shared</c>, so only pass the pairs that genuinely share something.</summary>
    public static BusSharedGuildResolver SharedGuildsReturning(
        FakeMessageBus bus, params (string OtherUserId, string[] GuildIds)[] shared)
    {
        bus.RespondWith<GetSharedGuildsRequest, GetSharedGuildsResponse>(new GetSharedGuildsResponse
        {
            Shared = shared
                .Select(s => new SharedGuildsSummary { OtherUserId = s.OtherUserId, GuildIds = s.GuildIds.ToList() })
                .ToList(),
        });

        return SharedGuilds(bus);
    }
}
