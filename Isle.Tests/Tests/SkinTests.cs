using IsleBridge.Sdk.Models;

namespace Isle.Tests.Tests;

[TestFixture]
public class SkinCustomizerTests
{
    [Test]
    public void FromHex_ValidHex_ReturnsCorrectNormalizedColor()
    {
        // Act
        var color = SkinColor.FromHex("006400", 1.0);

        // Assert
        Assert.That(color.R, Is.EqualTo(0.0));
        Assert.That(color.G, Is.EqualTo(100.0 / 255.0).Within(0.0001)); // 0x64 = 100
        Assert.That(color.B, Is.EqualTo(0.0));
        Assert.That(color.A, Is.EqualTo(1.0));
    }

    [Test]
    public void FromHex_NullHex_ReturnsDarkGreenDefault()
    {
        // Act
        var color = SkinColor.FromHex(null, 1.0);

        // Assert
        Assert.That(color.R, Is.EqualTo(0.0));
        Assert.That(color.G, Is.EqualTo(0.39));
        Assert.That(color.B, Is.EqualTo(0.0));
        Assert.That(color.A, Is.EqualTo(1.0));
    }

    [Test]
    public void FromProps_CompleteCommandInput_ParsesAllColorsCorrectly()
    {
        // Arrange
        var input = "body=808080 marks=808080 flank=808080 belly=808080 detail=808080 eyes=FFFFFF male=808080 teeth=FFFFFF mouth=FF8080 claws=505050 var=0 pat=0";

        // Act
        var customizer = SkinCustomizer.FromProps(input);

        // Assert
        Assert.That(customizer.BodyColor, Is.Not.Null);
        Assert.That(customizer.BodyColor!.R, Is.EqualTo(128.0 / 255.0).Within(0.0001)); // 0x80 = 128
        Assert.That(customizer.BodyColor.G, Is.EqualTo(128.0 / 255.0).Within(0.0001));
        Assert.That(customizer.BodyColor.B, Is.EqualTo(128.0 / 255.0).Within(0.0001));

        Assert.That(customizer.EyesColor, Is.Not.Null);
        Assert.That(customizer.EyesColor!.R, Is.EqualTo(1.0)); // 0xFF = 255
        Assert.That(customizer.EyesColor.G, Is.EqualTo(1.0));
        Assert.That(customizer.EyesColor.B, Is.EqualTo(1.0));

        Assert.That(customizer.MouthColor, Is.Not.Null);
        Assert.That(customizer.MouthColor!.R, Is.EqualTo(1.0)); // 0xFF = 255
        Assert.That(customizer.MouthColor.G, Is.EqualTo(128.0 / 255.0).Within(0.0001)); // 0x80 = 128
        Assert.That(customizer.MouthColor.B, Is.EqualTo(128.0 / 255.0).Within(0.0001)); // 0x80 = 128

        Assert.That(customizer.ClawsColor, Is.Not.Null);
        Assert.That(customizer.ClawsColor!.R, Is.EqualTo(80.0 / 255.0).Within(0.0001)); // 0x50 = 80
    }

    [Test]
    public void FromProps_MissingProperty_FallsBackToDarkGreenDefault()
    {
        // Arrange - omitting 'mouth' parameter
        var input = "body=808080 marks=808080 flank=808080 belly=808080 detail=808080 eyes=FFFFFF male=808080 teeth=FFFFFF claws=505050";

        // Act
        var customizer = SkinCustomizer.FromProps(input);

        // Assert
        Assert.That(customizer.MouthColor, Is.Not.Null);
        Assert.That(customizer.MouthColor!.R, Is.EqualTo(0.0));
        Assert.That(customizer.MouthColor.G, Is.EqualTo(0.39)); // Dark green default
        Assert.That(customizer.MouthColor.B, Is.EqualTo(0.0));
    }
    
    [Test]
    public void FromProps_IgnoresInvalidProperties()
    {
        // Arrange - adding a cli command
        var input = "generate body=808080 marks=808080 flank=808080 belly=808080 detail=808080 eyes=FFFFFF male=808080 teeth=FFFFFF claws=505050";

        // Act
        var customizer = SkinCustomizer.FromProps(input);

        // Assert
        Assert.That(customizer.MouthColor, Is.Not.Null);
        Assert.That(customizer.MouthColor!.R, Is.EqualTo(0.0));
        Assert.That(customizer.MouthColor.G, Is.EqualTo(0.39)); // Dark green default
        Assert.That(customizer.MouthColor.B, Is.EqualTo(0.0));
    }
}