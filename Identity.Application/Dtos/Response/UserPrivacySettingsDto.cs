using Domain;
using Identity.Domain.Enums;

namespace Identity.Application.Dtos.Response;

/// <summary>
/// The client-facing shape of <c>UserPrivacySettings</c> - the body of
/// <c>GET</c>/<c>PATCH api/v1/privacy-settings</c>, and the <c>privacySettings</c> key on
/// <c>GET api/v1/users/self</c>.
///
/// <para><b>Hand-written rather than a Facet.</b> A Facet would copy <c>Id</c>, <c>UserId</c>,
/// <c>CreatedAt</c> and <c>UpdatedAt</c> along with everything else, and the PATCH endpoint accepts
/// exactly the field names this type publishes - so the set of writable fields and the set of
/// returned fields have to be one list that is obviously one list. <c>Version</c> is returned
/// because a client that wants to detect a change made from another device needs it; it is not
/// writable.</para>
/// </summary>
public class UserPrivacySettingsDto
{
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

    /// <summary>Null means "keep forever", which is both the default and a meaningful value a PATCH
    /// may set explicitly - so <c>{"dmRetentionDays": null}</c> clears the window rather than being
    /// treated as an omission.</summary>
    public int? DmRetentionDays { get; set; }

    public ExplicitContentFilter ExplicitContentFilter { get; set; }

    public bool HidePushContent { get; set; }

    /// <summary>Read-only. Bumped by the server on every successful write.</summary>
    public int Version { get; set; }
}
