using System.ComponentModel.DataAnnotations.Schema;
using Domain;
using Identity.Domain.Enums;
using Persistence;

namespace Identity.Domain.Entities;

/// <summary>
/// The one owned, writable, cross-service-readable privacy record for an account.
/// </summary>
public class UserPrivacySettings : BaseEntity<UserPrivacySettings>, IPrefixedEntity
{
    [NotMapped] public static string Prefix { get; } = "upvs";

    public string UserId { get; set; } = null!;

    // ── Data use (consent; all default FALSE - opt-in, never opt-out) ──

    /// <summary>Whether telemetry may carry a real user identifier.</summary>
    public bool AllowDataCollection { get; set; }

    /// <summary>Gates any personalization signal.</summary>
    public bool AllowPersonalization { get; set; }

    /// <summary>Account-level, and <b>not</b> valid consent to record other participants - a clip
    /// feature must still capture per-session, per-participant consent at record time.</summary>
    public bool AllowVoiceRecordingInClips { get; set; }

    // ── Contactability ──

    public DirectMessagePolicy DirectMessagePolicy { get; set; } = DirectMessagePolicy.Friends;
    public FriendRequestPolicy FriendRequestPolicy { get; set; } = FriendRequestPolicy.Everyone;

    // ── Discoverability ──

    /// <summary>Exact-username lookup is how people find each other here, so this is the one
    /// discoverability default that is true.</summary>
    public bool DiscoverableByUsername { get; set; } = true;

    public bool DiscoverableByEmail { get; set; }
    public bool DiscoverableByPhone { get; set; }

    // ── Profile field visibility ──

    public Visibility MutualServersVisibility { get; set; } = Visibility.Friends;
    public Visibility MutualFriendsVisibility { get; set; } = Visibility.Friends;
    public Visibility ConnectionsVisibility { get; set; } = Visibility.Friends;
    public Visibility BirthdayVisibility { get; set; } = Visibility.Nobody;

    // ── Presence & activity ──

    /// <summary>"Playing Isle" and friends. Gates the activity half of presence projections.</summary>
    public bool ShareActivity { get; set; } = true;

    /// <summary>When false the account may still speak in non-positional channels but is not
    /// registered for positional capture.</summary>
    public bool AllowPositionalVoiceCapture { get; set; } = true;

    // ── Messaging behaviour ──

    /// <summary>Reciprocal at the enforcement site: an account that does not send read receipts does
    /// not receive them either.</summary>
    public bool SendReadReceipts { get; set; } = true;

    /// <summary>Reciprocal, same as <see cref="SendReadReceipts"/>.</summary>
    public bool SendTypingIndicators { get; set; } = true;

    /// <summary>Null means keep forever.</summary>
    public int? DmRetentionDays { get; set; }

    // ── Safety ──

    public ExplicitContentFilter ExplicitContentFilter { get; set; } = ExplicitContentFilter.UnknownSenders;

    // ── Push ──

    /// <summary>When set, every push for this account carries routing ids only - no body, no author
    /// name, no channel name. The encrypted-message path already does exactly this.</summary>
    public bool HidePushContent { get; set; }

    /// <summary>
    /// Bumped on every successful write and carried on <c>UserPrivacySettingsChangedEvent</c>.
    /// </summary>
    public int Version { get; set; }

    /// <summary>Mints the row an account starts life with.</summary>
    public static UserPrivacySettings CreateDefault(string userId, DateTimeOffset now)
    {
        return new UserPrivacySettings
        {
            Id = GenerateId(),
            UserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 0,
        };
    }
}
