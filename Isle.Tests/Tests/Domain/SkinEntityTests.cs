using Isle.Domain.Entity;
using IsleBridge.Sdk.Models;

namespace Isle.Tests.Tests.Domain;

/// <summary>Covers the domain Skin entity's own Create() factory - distinct from
/// SkinCustomizerTests (Tests/SkinTests.cs), which covers IsleBridge.Sdk.Models.SkinCustomizer.</summary>
[TestFixture]
public class SkinEntityTests
{
    [Test]
    public void Create_CopiesSpeciesPlayerIdAndCustomizer()
    {
        var customizer = new SkinCustomizer();

        var skin = Skin.Create(new CreateSkinParams
        {
            Species = IsleBridge.Sdk.Species.Triceratops,
            PlayerId = "player-1",
            Customizer = customizer,
        });

        Assert.That(skin.Species, Is.EqualTo(IsleBridge.Sdk.Species.Triceratops));
        Assert.That(skin.PlayerId, Is.EqualTo("player-1"));
        Assert.That(skin.Customizer, Is.SameAs(customizer));
    }

    [Test]
    public void CreateSkinParams_DefaultSpecies_IsTyrannosaurus()
    {
        var parameters = new CreateSkinParams();

        Assert.That(parameters.Species, Is.EqualTo(IsleBridge.Sdk.Species.Tyrannosaurus));
    }

    [Test]
    public void Create_GeneratesANonEmptyIdWithThePrefix()
    {
        var skin = Skin.Create(new CreateSkinParams { PlayerId = "player-1", Customizer = new SkinCustomizer() });

        Assert.That(skin.Id, Does.StartWith(Skin.Prefix));
    }
}
