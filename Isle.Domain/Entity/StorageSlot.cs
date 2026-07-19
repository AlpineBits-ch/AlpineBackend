using IsleBridge.Sdk.Models;

namespace Isle.Domain.Entity;

public class StorageSlot
{
    public string Species { get; set; }
    public long Hunger { get; set; }
    public long Health { get; set; }
    public long Thirst { get; set; }
    public long Stamina { get; set; }
    
    public int LifeCounter { get; set; }
    
    public MutationsData Mutations { get; set; }
}