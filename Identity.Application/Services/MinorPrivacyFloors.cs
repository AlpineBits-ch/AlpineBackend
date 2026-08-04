using Identity.Contracts.Bus.Response;
using Identity.Domain.Entities;
using Domain;
using Identity.Domain.Enums;

namespace Identity.Application.Services;

/// <summary>
/// The server-side privacy floors that apply to an account below the age of majority (T1-11).
///
/// <para><b>Two halves, and both are needed.</b> <see cref="Violates"/> refuses a write that
/// would breach a floor, so a minor cannot set a wider policy. <see cref="Clamp(UserPrivacySettings, bool)"/> narrows what is
/// <i>reported</i>, so a row that already holds a wider value - written before the account's birth
/// date said what it says now, or by a path that predates this code - cannot be acted on by any
/// service that reads the record. A refusal without a clamp is a control that depends on nothing
/// ever having gone wrong before.</para>
///
/// <para><b>Nothing is written to the row when a floor applies.</b> The stored value is left exactly
/// as the account last set it and the floor is applied on the way out. That is what makes the
/// birthday rollover of T1-11 work: on the day the account ages out, the clamp stops
/// narrowing and the settings the user originally chose are theirs again - unlocked, not silently
/// overwritten with the restricted values while they were a minor and left that way forever.</para>
///
/// <para>An account with no recorded birth date is <b>not</b> treated as a minor. Bot accounts have
/// no birth date, and so does every purged account; applying child protections to those would be a
/// guess dressed as a safeguard, and the accounts this protects always have a self-declared date
/// because registration requires one (<c>AgeValidator</c>).</para>
/// </summary>
public static class MinorPrivacyFloors
{
    /// <summary>The machine-readable code a refused write returns, alongside a <c>403</c>.</summary>
    public const string RestrictionCode = "minor_restriction";

    /// <summary>
    /// Whether the requested value for <paramref name="fieldName"/> breaches a floor.
    ///
    /// <para>Keyed on the camelCase name the API publishes so the caller can name the offending field
    /// in the refusal - a client told only "forbidden" has to guess which of nineteen fields to stop
    /// sending, and will usually guess by removing all of them.</para>
    /// </summary>
    /// <param name="fieldName">Field name as it appears in the PATCH body.</param>
    /// <param name="value">The parsed value the client asked for.</param>
    public static bool Violates(string fieldName, object? value) => fieldName switch
    {
        // Everyone is the one policy a minor may not choose. FriendsAndServerMembers is still
        // permitted: the spec's floor is "Friends", and a shared server is the ordinary way a young
        // player reaches the people they actually play with. Nothing wider than that.
        "directMessagePolicy" => value is DirectMessagePolicy.Everyone,

        // "Forced false and not settable" - only the widening direction is refused. Sending false is
        // a no-op that agrees with the floor, and refusing it would break a client that PATCHes its
        // whole state back with everything it read.
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
    ///
    /// <para><b>Mutates the instance it is given</b>, so callers must pass an untracked copy or a
    /// record they intend to persist at those values. Every current caller reads with
    /// <c>AsNoTracking</c> or maps immediately afterwards - see <see cref="Snapshot"/>, which exists
    /// so a tracked entity can be clamped without the clamp becoming a database write.</para>
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

    /// <summary>
    /// The same floors over the cross-service bus projection.
    ///
    /// <para>Applied here as well as at the REST surface because a floor that only narrows the
    /// account's own view of its settings is not a protection - Messaging resolves the DM policy from
    /// this summary, not from the endpoint, and would happily admit "Everyone" for a minor whose row
    /// somehow holds it. A separate method rather than a shared interface because the summary is a
    /// contract type in another assembly and giving it a domain interface would put Identity's domain
    /// on the wire.</para>
    /// </summary>
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
    /// A detached copy of <paramref name="settings"/> with the floors applied - the safe way to clamp
    /// something the change tracker is watching.
    ///
    /// <para>Without this, clamping a tracked entity for the sake of a response body would mark those
    /// properties modified, and the next <c>SaveChanges</c> anywhere in the request would persist the
    /// narrowed values over the user's real choices. On the day they age out they would then find
    /// their settings had been quietly rewritten rather than unlocked, which is precisely the failure
    /// T1-11 names.</para>
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
