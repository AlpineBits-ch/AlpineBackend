using Domain;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Isle.Api.Services.Privacy;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Wolverine;

namespace Isle.Tests.Helpers;

/// <summary>
/// Builds Isle's privacy stack over a substituted <see cref="IMessageBus"/>: a <see
/// cref="PrivacySettingsCache"/> on a <see cref="FakeDistributedCache"/> plus the <see
/// cref="PositionalVoiceConsent"/> gate on top of it.
/// </summary>
internal static class PrivacyTestFactory
{
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
        AllowVoiceRecordingInClips = false,
        SendReadReceipts = true,
        SendTypingIndicators = true,
        ExplicitContentFilter = ExplicitContentFilter.UnknownSenders,
        Version = 1,
    };

    internal sealed class Bundle
    {
        public required IMessageBus Bus { get; init; }
        public required FakeDistributedCache Cache { get; init; }
        public required PrivacySettingsCache Settings { get; init; }
        public required PositionalVoiceConsent Consent { get; init; }
    }

    /// <summary>
    /// A stack whose Identity answers with <paramref name="settings"/> (defaults for any id not
    /// named), or - when <paramref name="lookupFails"/> - throws, which is the fail-closed case.
    /// </summary>
    public static Bundle Build(
        IEnumerable<UserPrivacySettingsSummary>? settings = null,
        bool lookupFails = false,
        FakeDistributedCache? cache = null)
    {
        var byId = (settings ?? []).ToDictionary(s => s.UserId, s => s, StringComparer.Ordinal);
        var bus = Substitute.For<IMessageBus>();

        var call = bus.InvokeAsync<GetUserPrivacySettingsResponse>(
            Arg.Any<GetUserPrivacySettingsRequest>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());

        if (lookupFails)
        {
            call.ThrowsAsync(new InvalidOperationException("identity is down"));
        }
        else
        {
            call.Returns(ci =>
            {
                var request = (GetUserPrivacySettingsRequest)ci[0];
                return Task.FromResult(new GetUserPrivacySettingsResponse
                {
                    Settings = request.UserIds
                        .Select(id => byId.TryGetValue(id, out var found) ? found : Defaults(id))
                        .ToList(),
                });
            });
        }

        var store = cache ?? new FakeDistributedCache();
        var settingsCache = new PrivacySettingsCache(store, bus, NullLogger<PrivacySettingsCache>.Instance);

        return new Bundle
        {
            Bus = bus,
            Cache = store,
            Settings = settingsCache,
            Consent = new PositionalVoiceConsent(settingsCache, NullLogger<PositionalVoiceConsent>.Instance),
        };
    }

    /// <summary>Shorthand for the two states the T2-19 gate cares about.</summary>
    public static UserPrivacySettingsSummary WithPositionalVoice(string userId, bool allowed)
    {
        var settings = Defaults(userId);
        settings.AllowPositionalVoiceCapture = allowed;
        return settings;
    }
}
