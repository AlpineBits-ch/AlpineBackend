using Identity.Contracts.Bus.Response;
using Identity.Domain.Entities;
using Domain;
using Identity.Domain.Enums;

namespace Identity.Application.Services;

/// <summary>
/// The server-side privacy floors that apply to an account below the age of majority (T1-11).
/// </summary>
public static class MinorPrivacyFloors
{
    /// <summary>The machine-readable code a refused write returns, alongside a <c>403</c>.</summary>
    public const string RestrictionCode = "minor_restriction";

    /// <summary>
    /// Whether the requested value for <paramref name="fieldName"/> breaches a floor.
    /// </summary>
    /// <param name="fieldName">Field name as it appears in the PATCH body.</param>
    /// <param name="value">The parsed value the client asked for.</param>
    public static bool Violates(string fieldName, object? value) => fieldName switch
    {
        // Everyone is the one policy a minor may not choose.
        "directMessagePolicy" => value is DirectMessagePolicy.Everyone,

        // "Forced false and not settable" - only the widening direction is refused.
        "allowPersonalization" => value is true,
        "discoverableByEmail" => value is true,
        "discoverableByPhone" => value is true,
        "allowVoiceRecordingInClips" => value is true,

        // Floor of UnknownSenders: Off is refused, Everyone (stricter) is fine.
        "explicitContentFilter" => value is ExplicitContentFilter.Off,

        _ => false,
    };

    /// <summary>
    /// Narrows a settings record to the floors, for reporting and for cross-service enforcement.
    /// </summary>
    public static UserPrivacySettings Clamp(UserPrivacySettings settings, bool isMinor)
    {
        if (!isMinor) return settings;

        if (settings.DirectMessagePolicy == DirectMessagePolicy.Everyone)
            settings.DirectMessagePolicy = DirectMessagePolicy.Friends;

        settings.AllowPersonalization = false;
        settings.DiscoverableByEmail = false;
        settings.DiscoverableByPhone = false;
        settings.AllowVoiceRecordingInClips = false;

        if (settings.ExplicitContentFilter == ExplicitContentFilter.Off)
            settings.ExplicitContentFilter = ExplicitContentFilter.UnknownSenders;

        return settings;
    }

    /// <summary>The same floors over the cross-service bus projection.</summary>
    public static UserPrivacySettingsSummary Clamp(UserPrivacySettingsSummary summary, bool isMinor)
    {
        if (!isMinor) return summary;

        if (summary.DirectMessagePolicy == DirectMessagePolicy.Everyone)
            summary.DirectMessagePolicy = DirectMessagePolicy.Friends;

        summary.AllowPersonalization = false;
        summary.DiscoverableByEmail = false;
        summary.DiscoverableByPhone = false;
        summary.AllowVoiceRecordingInClips = false;

        if (summary.ExplicitContentFilter == ExplicitContentFilter.Off)
            summary.ExplicitContentFilter = ExplicitContentFilter.UnknownSenders;

        return summary;
    }

    /// <summary>
    /// A detached copy of <paramref name="settings"/> with the floors applied - the safe way to
    /// clamp something the change tracker is watching.
    /// </summary>
    public static UserPrivacySettings Snapshot(UserPrivacySettings settings, bool isMinor)
    {
        if (!isMinor) return settings;

        return Clamp(new UserPrivacySettings
        {
            Id = settings.Id,
            UserId = settings.UserId,
            CreatedAt = settings.CreatedAt,
            UpdatedAt = settings.UpdatedAt,
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
        }, isMinor: true);
    }
}
