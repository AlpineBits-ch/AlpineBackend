using Identity.Contracts.Bus.Response;
using Microsoft.EntityFrameworkCore;
using Social.Domain.Aggregate;
using Social.Infrastructure.Persistence;

namespace Social.Api.Services;

/// <summary>The identifier a directory lookup was keyed on.</summary>
public enum DirectoryKey
{
    /// <summary>An exact username. The only key this product can actually resolve.</summary>
    Username,

    /// <summary>An email address. No resolver exists - see <see cref="UserDirectory"/>.</summary>
    Email,

    /// <summary>A phone number. No resolver exists - see <see cref="UserDirectory"/>.</summary>
    Phone,
}

/// <summary>A resolved, discoverable subject and the privacy record that admitted them.</summary>
public sealed record DirectoryMatch(Profile Profile, UserPrivacySettingsSummary Settings);

/// <summary>
/// The one place a user is found by something a human typed (privacy spec T2-16).
/// </summary>
public class UserDirectory(MicroserviceContext ctx, PrivacySettingsCache privacySettings)
{
    /// <summary>
    /// Finds the subject named by <paramref name="value"/> under <paramref name="key"/>, or null if
    /// there is no such subject or they have switched that key off.
    /// </summary>
    public async Task<DirectoryMatch?> FindAsync(DirectoryKey key, string value, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var profile = await ResolveAsync(key, value, token);
        if (profile is null) return null;

        var settings = await privacySettings.GetAsync(profile.UserId, token);

        return IsDiscoverableBy(settings, key) ? new DirectoryMatch(profile, settings) : null;
    }

    /// <summary>The resolver table.</summary>
    private Task<Profile?> ResolveAsync(DirectoryKey key, string value, CancellationToken token) => key switch
    {
        DirectoryKey.Username => ctx.Profiles.FirstOrDefaultAsync(p => p.UserName == value, token),
        DirectoryKey.Email => Task.FromResult<Profile?>(null),
        DirectoryKey.Phone => Task.FromResult<Profile?>(null),
        // A key this build does not know about resolves to nothing rather than guessing.
        _ => Task.FromResult<Profile?>(null),
    };

    /// <summary>The <c>Discoverable*</c> flag that governs <paramref name="key"/>.</summary>
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
