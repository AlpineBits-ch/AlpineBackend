using System.ComponentModel.DataAnnotations.Schema;
using Domain;
using Identity.Domain.Enums;
using Persistence;

namespace Identity.Domain.Entities;

/// <summary>
/// The one owned, writable, cross-service-readable privacy record for an account. 1:1 with
/// <c>ApplicationUser</c>, in its own table.
///
/// <para><b>Why not on <see cref="UserPreferences"/>.</b> That entity is projected wholesale through
/// a Facet onto <c>GET /users/self</c>, so anything added there becomes part of a payload shape
/// nobody chose; privacy settings need their own endpoint, their own defaults and their own change
/// event, and every one of those would have had to be bolted onto a type that already means
/// something else.</para>
///
/// <para><b>Why explicit columns and not a <c>[Flags]</c> enum.</b> The legacy
/// <see cref="PrivacySettings"/> flags enum is not queryable ("every account that allows data
/// collection" is a bitwise scan), cannot carry per-field defaults, and under the Npgsql enum
/// mapping serializes to a comma-joined string - which means adding a member silently changes the
/// wire shape of every existing row.</para>
///
/// <para><b>Consent defaults to false, everything else to the least surprising value.</b> The three
/// data-use flags are opt-in and never opt-out: a row minted for a brand-new account must not be
/// able to represent consent the user was never asked for.</para>
/// </summary>
public class UserPrivacySettings : BaseEntity<UserPrivacySettings>, IPrefixedEntity
{
    [NotMapped] public static string Prefix { get; } = "upvs";

    public string UserId { get; set; } = null!;

    // ── Data use (consent; all default FALSE - opt-in, never opt-out) ──

    /// <summary>Whether telemetry may carry a real user identifier. Off means error reports are
    /// pseudonymized to a per-install id - see T0-4 and <c>AppEnvironment/SentryPrivacy.cs</c>.</summary>
    public bool AllowDataCollection { get; set; }

    /// <summary>Gates any personalization signal. There is no such pipeline today; the gate exists
    /// so one cannot be added without passing through it.</summary>
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

    /// <summary>Null means keep forever. Applies only to messages this account <i>sent</i> - deleting
    /// the other side's messages out of their own history is not this user's right to exercise.</summary>
    public int? DmRetentionDays { get; set; }

    // ── Safety ──

    public ExplicitContentFilter ExplicitContentFilter { get; set; } = ExplicitContentFilter.UnknownSenders;

    // ── Push ──

    /// <summary>When set, every push for this account carries routing ids only - no body, no author
    /// name, no channel name. The encrypted-message path already does exactly this.</summary>
    public bool HidePushContent { get; set; }

    /// <summary>
    /// Bumped on every successful write and carried on <c>UserPrivacySettingsChangedEvent</c>.
    ///
    /// <para>It is what lets a consuming service's cache decide whether the copy it holds is older
    /// than the one an event announces, rather than trusting delivery order it does not control.</para>
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// Mints the row an account starts life with. Every value is the field default above; nothing
    /// here reads the legacy <see cref="UserPreferences"/> flags, because a brand-new account has
    /// none.
    /// </summary>
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
