using Identity.Contracts.Bus.Response;
using Microsoft.EntityFrameworkCore;
using Social.Domain.Aggregate;
using Social.Infrastructure.Persistence;

namespace Social.Api.Services;

/// <summary>
/// The identifier a directory lookup was keyed on. One member per
/// <c>Discoverable*</c> flag on the privacy record, and that correspondence is the point - see
/// <see cref="UserDirectory"/>.
/// </summary>
public enum DirectoryKey
{
    /// <summary>An exact username. The only key this product can actually resolve.</summary>
    Username,

    /// <summary>An email address. No resolver exists - see <see cref="UserDirectory"/>.</summary>
    Email,

    /// <summary>A phone number. No resolver exists - see <see cref="UserDirectory"/>.</summary>
    Phone,
}

/// <summary>A resolved, discoverable subject and the privacy record that admitted them. Carrying the
/// settings out avoids a second lookup at the call site for the policy checks that follow.</summary>
public sealed record DirectoryMatch(Profile Profile, UserPrivacySettingsSummary Settings);

/// <summary>
/// The one place a user is found by something a human typed (privacy spec T2-16).
///
/// <para><b>Why this class exists at all.</b> An audit of the whole solution found no way to look a
/// user up by email or by phone: Social's <c>Profile</c> stores neither, Identity's only
/// email-keyed queries are login, password reset, email verification and registration uniqueness
/// (all self-service, all about the caller's own account) plus the admin DSR intake, and there is no
/// contact import, no invite-by-email and no federated <c>acct:</c> resolution. So
/// <c>DiscoverableByEmail</c> and <c>DiscoverableByPhone</c> gated nothing. Rather than invent a
/// lookup to justify two settings, the enforcement point is placed where a lookup would have to be
/// added: <see cref="FindAsync"/> resolves <i>and</i> gates in one step, and
/// <see cref="ResolveAsync"/> is the only resolver table. Someone adding email lookup writes one
/// line there and gets <c>DiscoverableByEmail</c> applied whether or not they had heard of it.</para>
///
/// <para><b>Refusals are indistinguishable.</b> "No such identifier" and "that person is not
/// discoverable by this key" are both <c>null</c>, so a caller cannot use the difference as an
/// account-enumeration oracle (cross-cutting rule 5). The two paths do differ by one cache read of
/// timing - the not-found path never reaches the privacy lookup - which is documented in the spec
/// rather than papered over with a fake round trip.</para>
///
/// <para><b>Fails closed.</b> The privacy record is resolved through
/// <see cref="PrivacySettingsCache"/>, whose restrictive defaults set every <c>Discoverable*</c>
/// flag to false, so an Identity outage makes everybody undiscoverable rather than everybody
/// discoverable.</para>
/// </summary>
public class UserDirectory(MicroserviceContext ctx, PrivacySettingsCache privacySettings)
{
    /// <summary>
    /// Finds the subject named by <paramref name="value"/> under <paramref name="key"/>, or null if
    /// there is no such subject <i>or</i> they have switched that key off. Callers must not try to
    /// tell those apart, and are given no means to.
    /// </summary>
    public async Task<DirectoryMatch?> FindAsync(DirectoryKey key, string value, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var profile = await ResolveAsync(key, value, token);
        if (profile is null) return null;

        var settings = await privacySettings.GetAsync(profile.UserId, token);

        return IsDiscoverableBy(settings, key) ? new DirectoryMatch(profile, settings) : null;
    }

    /// <summary>
    /// The resolver table. <b>Add new lookups here and nowhere else</b> - a query written straight
    /// against <c>ctx.Profiles</c> at a call site is a lookup with no discoverability gate on it.
    ///
    /// <para><see cref="DirectoryKey.Email"/> and <see cref="DirectoryKey.Phone"/> resolve to null
    /// because Social's <c>Profile</c> holds neither value; a future implementation of either would
    /// have to ask Identity over the bus, and would still be gated by <see cref="FindAsync"/>.</para>
    /// </summary>
    private Task<Profile?> ResolveAsync(DirectoryKey key, string value, CancellationToken token) => key switch
    {
        DirectoryKey.Username => ctx.Profiles.FirstOrDefaultAsync(p => p.UserName == value, token),
        DirectoryKey.Email => Task.FromResult<Profile?>(null),
        DirectoryKey.Phone => Task.FromResult<Profile?>(null),
        // A key this build does not know about resolves to nothing rather than guessing.
        _ => Task.FromResult<Profile?>(null),
    };

    /// <summary>
    /// The <c>Discoverable*</c> flag that governs <paramref name="key"/>.
    ///
    /// <para>Public and static so the mapping can be asserted directly: the test that matters is the
    /// one proving each key reads its own flag and that an unrecognised key reads as false.</para>
    /// </summary>
    public static bool IsDiscoverableBy(UserPrivacySettingsSummary settings, DirectoryKey key) => key switch
    {
        DirectoryKey.Username => settings.DiscoverableByUsername,
        DirectoryKey.Email => settings.DiscoverableByEmail,
        DirectoryKey.Phone => settings.DiscoverableByPhone,
        // Fail closed, per the cross-cutting rules: a key added to the enum without a flag to govern
        // it is undiscoverable until somebody says which flag governs it.
        _ => false,
    };
}
