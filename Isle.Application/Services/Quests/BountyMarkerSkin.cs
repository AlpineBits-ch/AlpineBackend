using IsleBridge.Sdk.Models;

namespace Isle.Api.Services.Quests;

/// <summary>
/// The skin a hunted player wears.
///
/// <para>The first pass at this was leucistic — bone-white body, chalk flanks, near-white underbelly.
/// It read as "that one is marked" from across a valley, which took the hunt away from the target's
/// side entirely: they could not use cover, could not sit still in a treeline, could not do anything
/// but run in the open until the timer ran out. A bounty is supposed to be a hunt, and a hunt needs
/// the quarry to have a chance.</para>
///
/// <para>So this is the other end of the same idea — a dark sooty morph. Body, flanks and underbelly
/// sit in the damp-bark range the map is already full of, with ordinary countershading, so at range,
/// under canopy, or at night the target still resolves as just another animal. What identifies them is
/// small and close: dried-blood markings down the body, rust on the detail and display, amber eyes,
/// and a dark bloodied mouth over bone teeth. A hunter who knows the marks and gets a proper look at
/// them will know. A hunter scanning the horizon will not.</para>
///
/// <para>Every channel stays at or below 1.0. Values above one render as HDR glow, which would light
/// the target up in exactly the conditions — night, shade, deep jungle — the camouflage exists for.</para>
///
/// <para>One scheme for every species. Per-species entries can be added to <see cref="Overrides"/>
/// later if a particular model wears it badly — the pattern index is deliberately left null, since a
/// per-species index outside the valid range silently drops the entire skin rebuild.</para>
/// </summary>
public static class BountyMarkerSkin
{
    private static readonly SkinCustomizer Default = new()
    {
        BodyColor = SkinColor.FromHex("4A4136"),        // damp bark
        MarkingsColor = SkinColor.FromHex("6E2B20"),    // dried blood — the tell, at conversational range
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
