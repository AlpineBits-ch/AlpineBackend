namespace Discovery.Api.Dtos.Request;

/// <summary>The whole listing draft write. Topics are wire strings, "tag:..." or "game:...", the
/// same grammar as <see cref="UpdateInterestsDto"/>.</summary>
public class UpsertListingDraftDto
{
    public string Headline { get; init; } = "";
    public string Pitch { get; init; } = "";
    public string Language { get; init; } = "en";
    public string JoinPolicy { get; init; } = "Open";
    public IReadOnlyList<string> Topics { get; init; } = [];
    public IReadOnlyList<string> Links { get; init; } = [];
}
