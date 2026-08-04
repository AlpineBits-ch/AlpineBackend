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
    nameof(ApplicationUser.Interests), nameof(ApplicationUser.UserPrivacySettings),
    NestedFacets = [typeof(EncryptedMasterKeyDto), typeof(UserPreferencesDto), typeof(UserDeviceDto), typeof(UserKeyPackageDto)])]
public partial class ApplicationUserDto
{
    /// <summary>
    /// The account's privacy record (T0-1), under its own key.
    ///
    /// <para><b>Additive.</b> The legacy <c>userPreferences</c> block above still carries
    /// <c>privacySettings</c> (the flags enum) and <c>directMessageSettings</c> unchanged, so a v1
    /// client parsing this payload is unaffected; the enforced values live here.</para>
    ///
    /// <para>Excluded from the facet and set by hand rather than routed through
    /// <c>NestedFacets</c>: the entity's shape is not the shape this key publishes - <c>Id</c>,
    /// <c>UserId</c> and the timestamps have no business on a self payload - and a facet over an EF
    /// entity here would put a second, differently-shaped copy of the privacy record on the wire.
    /// Null when the producer did not populate it; see <c>UserController.GetSelfAsync</c>.</para>
    /// </summary>
    public UserPrivacySettingsDto? PrivacySettings { get; set; }

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

    /// <summary>
    /// Legal documents whose current version this account has not accepted yet (T1-10).
    ///
    /// <para><b>Additive, and empty is the normal case.</b> A v1 client that does not know this key
    /// exists is unaffected; a client that does knows to show the acceptance screen without polling a
    /// second endpoint on every launch. Populated only when a version has actually been published
    /// that the account has not consented to - publishing a new Terms leaves every existing consent
    /// record intact and puts the account here instead, which is the whole mechanism.</para>
    ///
    /// <para>Never null: an empty array means "nothing owed", and a null would leave every client to
    /// decide for itself whether that meant "nothing owed" or "the server did not tell me".</para>
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
