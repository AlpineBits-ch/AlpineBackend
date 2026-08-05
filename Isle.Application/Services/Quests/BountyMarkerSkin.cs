using IsleBridge.Sdk.Models;

namespace Isle.Api.Services.Quests;

/// <summary>The skin a hunted player wears.</summary>
public static class BountyMarkerSkin
{
    private static readonly SkinCustomizer Default = new()
    {
        BodyColor = SkinColor.FromHex("4A4136"),        // damp bark
        MarkingsColor = SkinColor.FromHex("6E2B20"),    // dried blood - the tell, at conversational range
        FlankColor = SkinColor.FromHex("3B342B"),       // deeper, sits down into shadow
        UnderbellyColor = SkinColor.FromHex("57503F"),  // ordinary countershading, nothing pale
        Detail1Color = SkinColor.FromHex("7A3527"),     // rust, same story as the markings
        EyesColor = SkinColor.FromHex("D89A32"),        // amber; tiny surface, unmistakable head-on
        MaleDisplayColor = SkinColor.FromHex("8A3B29"), // rust
        TeethColor = SkinColor.FromHex("C9BFA6"),       // bone
        MouthColor = SkinColor.FromHex("5E1C17"),       // old blood, not fresh red
        ClawsColor = SkinColor.FromHex("241F1A"),       // near black, dirty
    };

    private static readonly Dictionary<string, SkinCustomizer> Overrides =
        new(StringComparer.OrdinalIgnoreCase);

    public static SkinCustomizer For(string? species) =>
        species is not null && Overrides.TryGetValue(species, out var skin) ? skin : Default;
}
