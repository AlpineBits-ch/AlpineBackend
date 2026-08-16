using Social.Api.Services;

namespace Social.Tests.Controllers;

public class ImageCacheHeaderTests
{
    /// <summary>The redirect must expire no later than the URL it points at.</summary>
    [Test]
    public void MaxAge_never_outlives_the_window()
    {
        var now = new DateTime(2026, 8, 16, 10, 0, 1, DateTimeKind.Utc);
        var seconds = (int)(FileService.WindowEnd(now) - now).TotalSeconds;

        Assert.That(seconds, Is.InRange(1, 3600));
    }

    [Test]
    public void MaxAge_is_positive_immediately_after_a_boundary()
    {
        var justAfter = new DateTime(2026, 8, 16, 11, 0, 0, DateTimeKind.Utc).AddTicks(1);
        var seconds = (int)(FileService.WindowEnd(justAfter) - justAfter).TotalSeconds;

        Assert.That(seconds > 0, Is.True);
    }
}
