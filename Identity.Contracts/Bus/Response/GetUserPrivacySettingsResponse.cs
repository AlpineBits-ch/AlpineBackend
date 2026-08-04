using Domain;

namespace Identity.Contracts.Bus.Response;

public class GetUserPrivacySettingsResponse
{
    public ICollection<UserPrivacySettingsSummary> Settings { get; set; } = new List<UserPrivacySettingsSummary>();
}

/// <summary>One account's privacy record as other services see it.</summary>
public class UserPrivacySettingsSummary
{
    public string UserId { get; set; } = null!;

    public bool AllowDataCollection { get; set; }
    public bool AllowPersonalization { get; set; }
    public bool AllowVoiceRecordingInClips { get; set; }

    public DirectMessagePolicy DirectMessagePolicy { get; set; }
    public FriendRequestPolicy FriendRequestPolicy { get; set; }

    public bool DiscoverableByUsername { get; set; }
    public bool DiscoverableByEmail { get; set; }
    public bool DiscoverableByPhone { get; set; }

    public Visibility MutualServersVisibility { get; set; }
    public Visibility MutualFriendsVisibility { get; set; }
    public Visibility ConnectionsVisibility { get; set; }
    public Visibility BirthdayVisibility { get; set; }

    public bool ShareActivity { get; set; }
    public bool AllowPositionalVoiceCapture { get; set; }

    public bool SendReadReceipts { get; set; }
    public bool SendTypingIndicators { get; set; }
    public int? DmRetentionDays { get; set; }

    public ExplicitContentFilter ExplicitContentFilter { get; set; }

    public bool HidePushContent { get; set; }

    /// <summary>Monotonic per account.</summary>
    public int Version { get; set; }
}
