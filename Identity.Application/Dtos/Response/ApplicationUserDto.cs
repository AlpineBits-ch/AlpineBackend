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
    nameof(ApplicationUser.Interests),
    NestedFacets = [typeof(EncryptedMasterKeyDto), typeof(UserPreferencesDto), typeof(UserDeviceDto), typeof(UserKeyPackageDto)])]
public partial class ApplicationUserDto
{
    /// <summary>
    /// The onboarding answer, as a lowercase name array rather than the domain's flags enum.
    ///
    /// <para><c>ApplicationUser.Interests</c> is excluded from the facet above and reshaped here
    /// because a <c>[Flags]</c> enum under this service's <c>JsonStringEnumConverter</c> serializes
    /// to the single string <c>"Isle, Social"</c> - a comma-separated list dressed as a string,
    /// which every client would have to split by hand and which silently changes shape when a
    /// third member is added.</para>
    ///
    /// <para>Populated by whoever builds the DTO; see <c>UserController.GetSelfAsync</c>. It is not
    /// filled by the generated facet constructor, so a new producer of this type has to set it
    /// deliberately - which is the right failure mode, since the alternative is a silently empty
    /// array that reads as "this account chose nothing".</para>
    /// </summary>
    public string[] Interests { get; set; } = [];
}

[Facet(typeof(EncryptedMasterKey))]
public partial class EncryptedMasterKeyDto
{

}

[Facet(typeof(UserPreferences))]
public partial class UserPreferencesDto
{

}
