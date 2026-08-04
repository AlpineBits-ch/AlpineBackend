using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Domain;
using Messaging.Application.Services.Privacy;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Wolverine;

namespace Messaging.Tests.Helpers;

/// <summary>
/// Builds the privacy stack (<see cref="PrivacySettingsCache"/>, <see cref="BlockCache"/>, <see
/// cref="DirectMessagePolicyService"/>, <see cref="ExplicitContentGuard"/>) over a test's existing
/// <see cref="FakeMessageBus"/>.
/// </summary>
internal static class TestPrivacyServices
{
    /// <summary>The product default, and therefore the default here: friends only.</summary>
    public static UserPrivacySettingsSummary Defaults(string userId) => new()
    {
        UserId = userId,
        DirectMessagePolicy = DirectMessagePolicy.Friends,
        FriendRequestPolicy = FriendRequestPolicy.Everyone,
        DiscoverableByUsername = true,
        MutualServersVisibility = Visibility.Friends,
        MutualFriendsVisibility = Visibility.Friends,
        ConnectionsVisibility = Visibility.Friends,
        BirthdayVisibility = Visibility.Nobody,
        ShareActivity = true,
        AllowPositionalVoiceCapture = true,
        SendReadReceipts = true,
        SendTypingIndicators = true,
        ExplicitContentFilter = ExplicitContentFilter.UnknownSenders,
        Version = 1,
    };

    public static UserPrivacySettingsSummary With(string userId, Action<UserPrivacySettingsSummary> mutate)
    {
        var settings = Defaults(userId);
        mutate(settings);
        return settings;
    }

    internal sealed class Bundle
    {
        public required PrivacySettingsCache Privacy { get; init; }
        public required BlockCache Blocks { get; init; }
        public required DirectMessagePolicyService Policy { get; init; }
        public required ExplicitContentGuard Content { get; init; }

        /// <summary>The bus the services see.</summary>
        public required FakeMessageBus Bus { get; init; }
    }

    /// <summary>A shared-guild lookup with a fixed answer, so the FriendsAndServerMembers branch
    /// can be exercised from both sides without a live Guild.</summary>
    internal sealed class StubSharedGuildLookup(bool answer) : ISharedGuildDirectMessageLookup
    {
        public Task<bool> SharesDirectMessageEnabledGuildAsync(
            string recipientUserId, string initiatorUserId, CancellationToken ct = default) =>
            Task.FromResult(answer);
    }

    /// <summary>A classifier that calls the named attachments explicit.</summary>
    internal sealed class StubMediaClassifier(params string[] explicitAttachmentIds) : IMediaClassifier
    {
        public bool IsOperational => true;

        public Task<IReadOnlyDictionary<string, MediaClassification>> ClassifyAsync(
            IReadOnlyCollection<MediaClassificationRequest> media, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, MediaClassification>>(
                media.ToDictionary(
                    m => m.AttachmentId,
                    m => explicitAttachmentIds.Contains(m.AttachmentId)
                        ? MediaClassification.Explicit
                        : MediaClassification.Safe,
                    StringComparer.Ordinal));
    }

    public static Bundle Build(
        FakeMessageBus inner,
        IEnumerable<UserPrivacySettingsSummary>? settings = null,
        IEnumerable<BlockRelationship>? blocks = null,
        bool sharesGuild = false,
        IMediaClassifier? classifier = null,
        FakeDistributedCache? cache = null,
        bool privacyLookupFails = false,
        bool blockLookupFails = false)
    {
        var settingsById = (settings ?? []).ToDictionary(s => s.UserId, s => s, StringComparer.Ordinal);
        var blockList = (blocks ?? []).ToList();

        var bus = new FakeMessageBus();
        bus.Responder = message => message switch
        {
            GetUserPrivacySettingsRequest r when privacyLookupFails =>
                throw new InvalidOperationException("identity is down"),

            GetUserPrivacySettingsRequest r => new GetUserPrivacySettingsResponse
            {
                Settings = r.UserIds
                    .Select(id => settingsById.TryGetValue(id, out var found) ? found : Defaults(id))
                    .ToList(),
            },

            GetBlockRelationshipsRequest when blockLookupFails =>
                throw new InvalidOperationException("social is down"),

            GetBlockRelationshipsRequest r => new GetBlockRelationshipsResponse
            {
                Blocks = blockList
                    .Where(b => r.UserIds.Contains(b.BlockerId) || r.UserIds.Contains(b.BlockedId))
                    .ToList(),
            },

            _ => inner.Responder is null
                ? throw new InvalidOperationException(
                    $"No responder configured for {message.GetType().Name}")
                : inner.Responder(message),
        };

        var store = cache ?? new FakeDistributedCache();

        var privacy = new PrivacySettingsCache(store, bus, NullLogger<PrivacySettingsCache>.Instance);
        var blockCache = new BlockCache(store, bus, NullLogger<BlockCache>.Instance);

        return new Bundle
        {
            Bus = bus,
            Privacy = privacy,
            Blocks = blockCache,
            Policy = new DirectMessagePolicyService(
                privacy, blockCache, new StubSharedGuildLookup(sharesGuild), bus,
                NullLogger<DirectMessagePolicyService>.Instance),
            Content = new ExplicitContentGuard(
                classifier ?? new NoOpMediaClassifier(), privacy, bus,
                NullLogger<ExplicitContentGuard>.Instance),
        };
    }
}
