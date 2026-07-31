using Facet;
using Identity.Domain.Aggregates;
using Identity.Domain.Entities;
using Identity.Domain.ValueObjects;

namespace Identity.Application.Dtos.Response;

/// <summary>The self view of an account (<c>GET /api/v1/users/self</c>).</summary>
[Facet(typeof(ApplicationUser), nameof(ApplicationUser.PasswordHash), nameof(ApplicationUser.SecurityStamp), nameof(ApplicationUser.ConcurrencyStamp),
    nameof(ApplicationUser.UserKeys), nameof(ApplicationUser.DeviceTokens), nameof(ApplicationUser.VoipTokens), nameof(ApplicationUser.Backups),
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
