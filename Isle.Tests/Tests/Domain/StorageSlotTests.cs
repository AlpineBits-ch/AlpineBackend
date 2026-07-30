using Isle.Domain.Entity;
using IsleBridge.Sdk.Models;

namespace Isle.Tests.Tests.Domain;

[TestFixture]
public class StorageSlotTests
{
    [Test]
    public void Create_CopiesAllParametersOntoTheSlot()
    {
        var health = new DinoHealthData { Hunger = 80, Health = 90, Thirst = 70, Stamina = 60 };
        var mutations = new MutationsData();

        var slot = StorageSlot.Create(new CreateStorageSlotParams
        {
            HealthData = health,
            Species = IsleBridge.Sdk.Species.Tyrannosaurus,
            Growth = 0.75,
            Mutations = mutations,
        });

        Assert.That(slot.Species, Is.EqualTo(IsleBridge.Sdk.Species.Tyrannosaurus));
        Assert.That(slot.Growth, Is.EqualTo(0.75));
        Assert.That(slot.HealthData, Is.SameAs(health));
        Assert.That(slot.Mutations, Is.SameAs(mutations));
    }

    [Test]
    public void Create_GeneratesANonEmptyIdWithThePrefix()
    {
        var slot = StorageSlot.Create(new CreateStorageSlotParams
        {
            HealthData = new DinoHealthData(),
            Species = IsleBridge.Sdk.Species.Tyrannosaurus,
            Mutations = new MutationsData(),
        });

        Assert.That(slot.Id, Does.StartWith(StorageSlot.Prefix));
    }

    [Test]
    public void Create_DefaultsIsDeployedToFalse()
    {
        var slot = StorageSlot.Create(new CreateStorageSlotParams
        {
            HealthData = new DinoHealthData(),
            Species = IsleBridge.Sdk.Species.Tyrannosaurus,
            Mutations = new MutationsData(),
        });

        Assert.That(slot.IsDeployed, Is.False);
    }

    [Test]
    public void FriendlySpeciesName_DelegatesToIsleBridgeSpeciesFriendlyName()
    {
        var slot = StorageSlot.Create(new CreateStorageSlotParams
        {
            HealthData = new DinoHealthData(),
            Species = IsleBridge.Sdk.Species.Tyrannosaurus,
            Mutations = new MutationsData(),
        });

        Assert.That(slot.FriendlySpeciesName(), Is.EqualTo(IsleBridge.Sdk.Species.FriendlyName(IsleBridge.Sdk.Species.Tyrannosaurus)));
    }
}
