namespace Isle.Domain.Entity;

/// <summary>Which of a player's skins they are actually wearing.</summary>
public static class SkinSelection
{
    /// <summary>The skin a player wears, or null when they have none at all.</summary>
    public static Skin? Effective(IEnumerable<Skin>? skins)
    {
        if (skins is null) return null;

        var all = skins as IReadOnlyCollection<Skin> ?? skins.ToList();
        if (all.Count == 0) return null;

        return Newest(all.Where(s => s.IsEquipped)) ?? Newest(all);
    }

    private static Skin? Newest(IEnumerable<Skin> skins) =>
        skins.OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id, StringComparer.Ordinal)
            .FirstOrDefault();
}
