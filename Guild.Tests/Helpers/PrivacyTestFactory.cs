using Guild.Application.Services;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;

namespace Guild.Tests.Helpers;

/// <summary>
/// Builds <see cref="PrivacySettingsCache"/> and <see cref="BlockCache"/> over the in-process
/// fakes, so a test can say "Identity says this" or "Social is unreachable" in one line.
/// </summary>
internal static class PrivacyTestFactory
{
    /// <summary>An account with everything at its permissive default - the shape almost every test
    /// starts from, so that the one field it overrides is the only thing under test.</summary>
    public static UserPrivacySettingsSummary Permissive(string userId) => new()
    {
        UserId = userId,
        AllowDataCollection = true,
        AllowPersonalization = true,
        AllowVoiceRecordingInClips = true,
        DirectMessagePolicy = DirectMessagePolicy.Everyone,
        FriendRequestPolicy = FriendRequestPolicy.Everyone,
        DiscoverableByUsername = true,
        DiscoverableByEmail = true,
        DiscoverableByPhone = true,
        MutualServersVisibility = Visibility.Everyone,
        MutualFriendsVisibility = Visibility.Everyone,
        ConnectionsVisibility = Visibility.Everyone,
        BirthdayVisibility = Visibility.Everyone,
        ShareActivity = true,
        AllowPositionalVoiceCapture = true,
        SendReadReceipts = true,
        SendTypingIndicators = true,
        ExplicitContentFilter = ExplicitContentFilter.Off,
        HidePushContent = false,
        Version = 1,
    };

    /// <summary>A cache whose bus answers with exactly <paramref name="settings"/>.</summary>
    public static PrivacySettingsCache Privacy(
        FakeInvokingMessageBus bus, FakeDistributedCache cache, params UserPrivacySettingsSummary[] settings)
    {
        bus.SetResponse<GetUserPrivacySettingsRequest>(
            new GetUserPrivacySettingsResponse { Settings = settings.ToList() });

        return new PrivacySettingsCache(cache, bus, NullLogger<PrivacySettingsCache>.Instance);
    }

    /// <summary>A cache whose bus never answers - "Identity is down".</summary>
    public static PrivacySettingsCache UnreachablePrivacy(FakeInvokingMessageBus bus, FakeDistributedCache cache)
    {
        bus.ClearResponses();
        return new PrivacySettingsCache(cache, bus, NullLogger<PrivacySettingsCache>.Instance);
    }

    /// <summary>A block cache whose bus answers with exactly <paramref name="blocks"/>, each given
    /// as (blocker, blocked).</summary>
    public static BlockCache Blocks(
        FakeInvokingMessageBus bus, FakeDistributedCache cache, params (string Blocker, string Blocked)[] blocks)
    {
        bus.SetResponse<GetBlockRelationshipsRequest>(new GetBlockRelationshipsResponse
        {
            Blocks = blocks
                .Select(b => new BlockRelationship { BlockerId = b.Blocker, BlockedId = b.Blocked })
                .ToList(),
        });

        return new BlockCache(cache, bus, NullLogger<BlockCache>.Instance);
    }

    /// <summary>A block cache whose bus never answers - "Social is down".</summary>
    public static BlockCache UnreachableBlocks(FakeInvokingMessageBus bus, FakeDistributedCache cache)
    {
        bus.ClearResponses();
        return new BlockCache(cache, bus, NullLogger<BlockCache>.Instance);
    }
}
