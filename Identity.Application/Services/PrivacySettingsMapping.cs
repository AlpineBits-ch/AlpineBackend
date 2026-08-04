using Identity.Application.Dtos.Response;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Entities;

namespace Identity.Application.Services;

/// <summary>
/// The two projections of <see cref="UserPrivacySettings"/> that leave this service: the client DTO
/// and the bus summary.
/// </summary>
public static class PrivacySettingsMapping
{
    public static UserPrivacySettingsDto ToDto(UserPrivacySettings settings) => new()
    {
        AllowDataCollection = settings.AllowDataCollection,
        AllowPersonalization = settings.AllowPersonalization,
        AllowVoiceRecordingInClips = settings.AllowVoiceRecordingInClips,
        DirectMessagePolicy = settings.DirectMessagePolicy,
        FriendRequestPolicy = settings.FriendRequestPolicy,
        DiscoverableByUsername = settings.DiscoverableByUsername,
        DiscoverableByEmail = settings.DiscoverableByEmail,
        DiscoverableByPhone = settings.DiscoverableByPhone,
        MutualServersVisibility = settings.MutualServersVisibility,
        MutualFriendsVisibility = settings.MutualFriendsVisibility,
        ConnectionsVisibility = settings.ConnectionsVisibility,
        BirthdayVisibility = settings.BirthdayVisibility,
        ShareActivity = settings.ShareActivity,
        AllowPositionalVoiceCapture = settings.AllowPositionalVoiceCapture,
        SendReadReceipts = settings.SendReadReceipts,
        SendTypingIndicators = settings.SendTypingIndicators,
        DmRetentionDays = settings.DmRetentionDays,
        ExplicitContentFilter = settings.ExplicitContentFilter,
        HidePushContent = settings.HidePushContent,
        Version = settings.Version,
    };

    public static UserPrivacySettingsSummary ToSummary(UserPrivacySettings settings) => new()
    {
        UserId = settings.UserId,
        AllowDataCollection = settings.AllowDataCollection,
        AllowPersonalization = settings.AllowPersonalization,
        AllowVoiceRecordingInClips = settings.AllowVoiceRecordingInClips,
        DirectMessagePolicy = settings.DirectMessagePolicy,
        FriendRequestPolicy = settings.FriendRequestPolicy,
        DiscoverableByUsername = settings.DiscoverableByUsername,
        DiscoverableByEmail = settings.DiscoverableByEmail,
        DiscoverableByPhone = settings.DiscoverableByPhone,
        MutualServersVisibility = settings.MutualServersVisibility,
        MutualFriendsVisibility = settings.MutualFriendsVisibility,
        ConnectionsVisibility = settings.ConnectionsVisibility,
        BirthdayVisibility = settings.BirthdayVisibility,
        ShareActivity = settings.ShareActivity,
        AllowPositionalVoiceCapture = settings.AllowPositionalVoiceCapture,
        SendReadReceipts = settings.SendReadReceipts,
        SendTypingIndicators = settings.SendTypingIndicators,
        DmRetentionDays = settings.DmRetentionDays,
        ExplicitContentFilter = settings.ExplicitContentFilter,
        HidePushContent = settings.HidePushContent,
        Version = settings.Version,
    };
}
