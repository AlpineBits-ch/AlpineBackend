using Identity.Application.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Identity.Tests;

/// <summary>
/// Reproduces a bug reported against the dual-pod deployment: a user's first verification attempt
/// is rejected even though they typed the code from the email they just received.
/// </summary>
[TestFixture]
public class VerificationCodeServiceTests
{
    private IDistributedCache _cache = null!;

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        _cache = services.BuildServiceProvider().GetRequiredService<IDistributedCache>();
    }

    [Test]
    public async Task GetOrCreateCodeAsync_CalledTwiceForSameEmail_DoesNotInvalidateThePreviouslyIssuedCode()
    {
        const string email = "racer@example.com";

        // First trigger: e.g. the automatic welcome email sent right after signup.
        var firstCode = await VerificationCodeService.GetOrCreateCodeAsync(_cache, email);

        // Second trigger for the same user before the first code expired: e.g. the
        // user clicks "resend code", or a duplicate delivery of the UserCreated
        // event is handled by the other pod.
        var secondCode = await VerificationCodeService.GetOrCreateCodeAsync(_cache, email);

        Assert.That(secondCode, Is.EqualTo(firstCode),
            "A repeat verification-code request must not invalidate a code that was already e-mailed to the user.");
    }
}
