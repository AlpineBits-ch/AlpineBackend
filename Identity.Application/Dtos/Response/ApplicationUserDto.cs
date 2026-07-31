using Facet;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.ValueObjects;

namespace Identity.Application.Dtos.Response;

/// <summary>
/// The self view of an account (<c>GET /api/v1/users/self</c>).
///
/// Every navigation is either dropped or pointed at a facet. UserKeys, PushTokens and Backups are
/// dropped outright - they are key material, push-notification tokens and device backup blobs,
/// none of which belong in a user response and none of which anything reads. The
/// rest go through nested facets so the response carries DTOs rather than tracked entities: as
/// entities they serialize their own back-reference to the user and loop.
/// </summary>
[Facet(typeof(ApplicationUser), nameof(ApplicationUser.PasswordHash), nameof(ApplicationUser.SecurityStamp), nameof(ApplicationUser.ConcurrencyStamp),
    nameof(ApplicationUser.UserKeys), nameof(ApplicationUser.PushTokens), nameof(ApplicationUser.Backups),
    NestedFacets = [typeof(EncryptedMasterKeyDto), typeof(UserPreferencesDto), typeof(UserDeviceDto), typeof(UserKeyPackageDto)])]
public partial class ApplicationUserDto
{

}

[Facet(typeof(EncryptedMasterKey))]
public partial class EncryptedMasterKeyDto
{

}

[Facet(typeof(UserPreferences))]
public partial class UserPreferencesDto
{

}
