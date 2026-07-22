using Isle.Domain.Aggregates;
using IsleBridge.Sdk.Models;
using Persistence;

namespace Isle.Domain.Entity;

public class DinoHealthData
{
    public long Hunger { get; set; }
    public long Health { get; set; }
    public long Thirst { get; set; }
    public long Stamina { get; set; }
}

public class CreateStorageSlotParams
{
    public DinoHealthData HealthData { get; set; }
    public string Species { get; set; }

    /// <summary>Growth of the dino at the moment it was stored (0..1), restored on load.</summary>
    public double Growth { get; set; }
    public MutationsData Mutations { get; set; }
}
public class StorageSlot : BaseEntity<StorageSlot>, IPrefixedEntity
{
    public Storage Storage { get; set; }
    public string StorageId { get; set; }
    public string Species { get; set; }

    /// <summary>Growth of the stored dino (0..1); re-applied when the dino is loaded back.</summary>
    public double Growth { get; set; }

    public DinoHealthData HealthData { get; set; }

    public MutationsData Mutations { get; set; }

    public static StorageSlot Create(CreateStorageSlotParams paramater)
    {
        var id = GenerateId();
        var date = DateTime.UtcNow;

        return new StorageSlot
        {
            Id = id,
            CreatedAt = date,
            UpdatedAt = date,
            Species = paramater.Species,
            Growth = paramater.Growth,
            HealthData = paramater.HealthData,
            Mutations = paramater.Mutations
        };
    }

    public static string Prefix { get; } = "slot";
}