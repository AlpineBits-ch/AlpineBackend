using Isle.Domain.Aggregates;
using IsleBridge.Sdk.Models;
using Persistence;

namespace Isle.Domain.Entity;

public class CreateSkinParams()
{
    public string Species { get; set; } = IsleBridge.Sdk.Species.Tyrannosaurus;
    public string PlayerId { get; set; }
    public SkinCustomizer Customizer { get; set; }

    /// <summary>What the player calls it. Blank falls back to the species name - see <see cref="Skin.Name"/>.</summary>
    public string? Name { get; set; }
}

public class Skin : BaseEntity<Skin>, IPrefixedEntity
{
    /// <summary>The longest name a player may give a skin.</summary>
    public const int MaxNameLength = 48;

    public virtual Player Player { get; set; }
    public string PlayerId { get; set; }

    public string Species { get; set; }

    /// <summary>The player's own label for this skin.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether this is the skin the player wears.</summary>
    public bool IsEquipped { get; set; }

    public SkinCustomizer Customizer { get; set; }
    public static string Prefix { get; } = "skin";

    /// <summary>Trims and truncates a player-supplied name, falling back to the species.</summary>
    public static string ResolveName(string? name, string species)
    {
        var trimmed = name?.Trim();

        if (string.IsNullOrEmpty(trimmed))
            return IsleBridge.Sdk.Species.FriendlyName(species);

        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
    }

    public static Skin Create(CreateSkinParams parameters)
    {
        var id = GenerateId();
        var date = DateTime.UtcNow;

        return new Skin()
        {
            Id = id,
            CreatedAt = date,
            UpdatedAt = date,
            Species = parameters.Species,
            Name = ResolveName(parameters.Name, parameters.Species),
            PlayerId = parameters.PlayerId,
            Customizer = parameters.Customizer
        };
    }
}
