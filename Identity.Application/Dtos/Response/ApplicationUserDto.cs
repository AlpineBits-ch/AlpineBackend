using Facet;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.ValueObjects;

namespace Identity.Application.Dtos.Response;

/// <summary>The self view of an account (<c>GET /api/v1/users/self</c>).</summary>
// PhoneVerifiedAt is excluded deliberately, and this is the only place that decision is
// enforceable.
[Facet(typeof(ApplicationUser), nameof(ApplicationUser.PasswordHash), nameof(ApplicationUser.SecurityStamp), nameof(ApplicationUser.ConcurrencyStamp),
    nameof(ApplicationUser.UserKeys), nameof(ApplicationUser.PushTokens), nameof(ApplicationUser.Backups),
    nameof(ApplicationUser.Interests), nameof(ApplicationUser.UserPrivacySettings),
    nameof(ApplicationUser.PhoneVerifiedAt),
    NestedFacets = [typeof(EncryptedMasterKeyDto), typeof(UserPreferencesDto), typeof(UserDeviceDto), typeof(UserKeyPackageDto)])]
public partial class ApplicationUserDto
{
    /// <summary>The account's privacy record (T0-1), under its own key.</summary>
    public UserPrivacySettingsDto? PrivacySettings { get; set; }

    /// <summary>
    /// The onboarding answer, as a lowercase name array rather than the domain's flags enum.
    /// </summary>
    public string[] Interests { get; set; } = [];

    /// <summary>
    /// Legal documents whose current version this account has not accepted yet (T1-10).
    /// </summary>
    public OutstandingConsentDto[] ConsentRequired { get; set; } = [];
}

[Facet(typeof(EncryptedMasterKey))]
public partial class EncryptedMasterKeyDto
{

}

[Facet(typeof(UserPreferences))]
public partial class UserPreferencesDto
{

}
