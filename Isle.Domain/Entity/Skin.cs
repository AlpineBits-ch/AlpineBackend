using Isle.Domain.Aggregates;
using IsleBridge.Sdk.Models;
using Persistence;

namespace Isle.Domain.Entity;

public class CreateSkinParams()
{
    public string Species { get; set; } = IsleBridge.Sdk.Species.Tyrannosaurus;
    public string PlayerId { get; set; }
    public SkinCustomizer Customizer { get; set; }
}

public class Skin : BaseEntity<Skin>, IPrefixedEntity
{
    public virtual Player Player { get; set; }
    public string PlayerId { get; set; }
    
    public string Species { get; set; }
    
    public SkinCustomizer Customizer { get; set; }
    public static string Prefix { get; } = "skin";
    
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
            PlayerId = parameters.PlayerId,
            Customizer = parameters.Customizer
        };
    }
}