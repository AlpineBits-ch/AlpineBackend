using FluentValidation.TestHelper;
using Guild.Domain.Aggregates;
using Guild.Domain.Enums;
using Guild.Domain.Validators;

namespace Guild.Tests.Domain;

[TestFixture]
public class ChannelValidatorTests
{
    private static Channel Channel(string? icon = null, string? iconColor = null) => new()
    {
        Name = "general",
        Type = ChannelType.Text,
        Icon = icon,
        IconColor = iconColor,
    };

    [TestCase(null)]
    [TestCase("")]
    [TestCase("volume-2")]
    [TestCase("a")]
    [TestCase("messages-square")]
    public void AcceptsAbsentEmptyOrWellFormedIcon(string? icon)
    {
        new ChannelValidator().TestValidate(Channel(icon: icon))
            .ShouldNotHaveValidationErrorFor(c => c.Icon);
    }

    [TestCase("Volume2")]
    [TestCase("volume 2")]
    [TestCase("pi pi-volume-up")]
    [TestCase("volume_2")]
    [TestCase("../../etc/passwd")]
    public void RejectsMalformedIcon(string icon)
    {
        new ChannelValidator().TestValidate(Channel(icon: icon))
            .ShouldHaveValidationErrorFor(c => c.Icon);
    }

    [Test]
    public void RejectsIconLongerThan48()
    {
        new ChannelValidator().TestValidate(Channel(icon: new string('a', 49)))
            .ShouldHaveValidationErrorFor(c => c.Icon);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("#4B5BC4")]
    [TestCase("#ffffff")]
    [TestCase("#AbCdEf")]
    public void AcceptsAbsentEmptyOrWellFormedColour(string? colour)
    {
        new ChannelValidator().TestValidate(Channel(iconColor: colour))
            .ShouldNotHaveValidationErrorFor(c => c.IconColor);
    }

    [TestCase("4B5BC4")]
    [TestCase("#4B5BC")]
    [TestCase("#4B5BC44")]
    [TestCase("red")]
    [TestCase("#GGGGGG")]
    public void RejectsMalformedColour(string colour)
    {
        new ChannelValidator().TestValidate(Channel(iconColor: colour))
            .ShouldHaveValidationErrorFor(c => c.IconColor);
    }
}
