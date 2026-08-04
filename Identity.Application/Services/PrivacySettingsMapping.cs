using Identity.Application.Dtos.Response;
using Identity.Contracts.Bus.Response;
using Identity.Domain.Entities;

namespace Identity.Application.Services;

/// <summary>
/// The two projections of <see cref="UserPrivacySettings"/> that leave this service: the client DTO
/// and the bus summary.
///
/// <para>Kept together on purpose. They carry the same fields for the same reason - every one of
/// them is an enforcement input - and a field added to the entity but to only one of these is a
/// setting the user can see and no service can honour, or the reverse.</para>
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
