using Identity.Application.Dtos.Response;

namespace Identity.Tests.Domain;

/// <summary>
/// What the self payload must and must not carry, for the two fields nothing else pins.
/// </summary>
[TestFixture]
public class ApplicationUserDtoTests
{
    /// <summary>
    /// Clients read the account's phone number off the self payload rather than through a second
    /// request, and use its presence to decide whether to offer "add a number" next to the
    /// per-household sharing toggle.
    /// </summary>
    [Test]
    public void PhoneNumber_IsPublishedOnTheSelfPayload()
    {
        Assert.That(typeof(ApplicationUserDto).GetProperty(nameof(ApplicationUserDto.PhoneNumber)),
            Is.Not.Null,
            "clients decide whether to prompt for a number by reading this; removing it breaks that "
            + "silently rather than loudly");
    }

    /// <summary>The mirror, and the more important half.</summary>
    [Test]
    public void PhoneVerifiedAt_IsNotPublished()
    {
        Assert.That(typeof(ApplicationUserDto).GetProperty("PhoneVerifiedAt"), Is.Null,
            "nothing verifies a phone number, so the wire must offer no way to suggest otherwise");
    }
}
