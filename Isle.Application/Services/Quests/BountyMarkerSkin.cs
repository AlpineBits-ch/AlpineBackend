using IsleBridge.Sdk.Models;

namespace Isle.Api.Services.Quests;

/// <summary>
/// The skin a hunted player wears.
///
/// <para>The brief was that it must read as "that one is marked" at a glance while still looking like
/// something that could walk out of the treeline — so this is a leucistic phenotype, not a flag.
/// Bone-white body, chalk-grey flanks, dead-pale underbelly, and the tell: a blood-stained mouth over
/// yellowed teeth and dark claws, the look of an animal that has been eating and hasn't cleaned up.
/// Real animals turn up looking like this; neon does not.</para>
///
/// <para>One scheme for every species. Per-species entries can be added to <see cref="Overrides"/>
/// later if a particular model wears it badly — the pattern index is deliberately left null, since a
/// per-species index outside the valid range silently drops the entire skin rebuild.</para>
/// </summary>
public static class BountyMarkerSkin
{
    private static readonly SkinCustomizer Default = new()
    {
        BodyColor = SkinColor.FromHex("D8D2C4"),        // bone white
        MarkingsColor = SkinColor.FromHex("B3A995"),    // faint sun-bleached markings
        FlankColor = SkinColor.FromHex("C2BBAC"),       // chalk grey
        UnderbellyColor = SkinColor.FromHex("EDE8DC"),  // pale, almost translucent
        Detail1Color = SkinColor.FromHex("A89C86"),
        EyesColor = SkinColor.FromHex("D6B25A"),        // pale amber
        MaleDisplayColor = SkinColor.FromHex("C4B69C"),
        TeethColor = SkinColor.FromHex("D9CBA3"),       // yellowed
        MouthColor = SkinColor.FromHex("8C2B22"),       // blood
        ClawsColor = SkinColor.FromHex("3B342C"),       // dark, dirty
    };

    private static readonly Dictionary<string, SkinCustomizer> Overrides =
        new(StringComparer.OrdinalIgnoreCase);

    public static SkinCustomizer For(string? species) =>
        species is not null && Overrides.TryGetValue(species, out var skin) ? skin : Default;
}
