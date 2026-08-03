namespace Identity.Application.Dtos.Request;

/// <summary>
/// The onboarding picker's answer: which halves of the product this account came for.
///
/// <para>Lowercase names (<c>["isle","social"]</c>), at least one required. See
/// <see cref="Identity.Domain.Enums.UserInterestsExtensions"/> for why the wire form is an array
/// rather than the serialized flags enum.</para>
/// </summary>
public class UpdateOnboardingDto
{
    public string[]? Interests { get; set; }
}
