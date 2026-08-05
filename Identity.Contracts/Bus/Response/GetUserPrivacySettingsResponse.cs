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

    /// <summary>
    /// Games this account has individually suppressed, even with <see cref="ShareActivity"/> on.
    /// </summary>
    public HiddenActivitySummary HiddenActivities { get; set; } = new();

    public bool AllowPositionalVoiceCapture { get; set; }

    public bool SendReadReceipts { get; set; }
    public bool SendTypingIndicators { get; set; }
    public int? DmRetentionDays { get; set; }

    public ExplicitContentFilter ExplicitContentFilter { get; set; }

    public bool HidePushContent { get; set; }

    /// <summary>Monotonic per account.</summary>
    public int Version { get; set; }
}

/// <summary>
/// The suppression set, split by which key each entry uses so consumers can match without
/// re-deriving it per activity.
/// </summary>
public class HiddenActivitySummary
{
    /// <summary>Application ids to suppress. Compared ordinally - these are numeric strings.</summary>
    public ICollection<string> ApplicationIds { get; set; } = new List<string>();

    /// <summary>Activity names to suppress, for sources that produce no application id.</summary>
    public ICollection<string> Names { get; set; } = new List<string>();

    /// <summary>Whether <paramref name="applicationId"/> or <paramref name="name"/> is suppressed.
    /// One helper so every consumer matches the same way rather than each inventing its own.</summary>
    public bool Suppresses(string? applicationId, string? name)
    {
        if (!string.IsNullOrEmpty(applicationId)
            && ApplicationIds.Contains(applicationId, StringComparer.Ordinal))
        {
            return true;
        }

        return !string.IsNullOrEmpty(name)
               && Names.Contains(name, StringComparer.OrdinalIgnoreCase);
    }
}
