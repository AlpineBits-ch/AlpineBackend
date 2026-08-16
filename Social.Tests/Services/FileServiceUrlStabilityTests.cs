using Social.Api.Services;

namespace Social.Tests.Services;

public class FileServiceUrlStabilityTests
{
    [Test]
    public void WindowEnd_rounds_up_to_the_next_hour()
    {
        var at = new DateTime(2026, 8, 16, 10, 59, 30, DateTimeKind.Utc);
        Assert.That(FileService.WindowEnd(at), Is.EqualTo(new DateTime(2026, 8, 16, 11, 0, 0, DateTimeKind.Utc)));
    }

    [Test]
    public void WindowEnd_is_identical_for_every_instant_inside_one_hour()
    {
        var early = new DateTime(2026, 8, 16, 10, 0, 1, DateTimeKind.Utc);
        var late = new DateTime(2026, 8, 16, 10, 59, 59, DateTimeKind.Utc);
        Assert.That(FileService.WindowEnd(early), Is.EqualTo(FileService.WindowEnd(late)));
    }

    [Test]
    public void WindowEnd_advances_across_an_hour_boundary()
    {
        var before = new DateTime(2026, 8, 16, 10, 59, 59, DateTimeKind.Utc);
        var after = new DateTime(2026, 8, 16, 11, 0, 1, DateTimeKind.Utc);
        Assert.That(FileService.WindowEnd(before), Is.Not.EqualTo(FileService.WindowEnd(after)));
    }

    [Test]
    public void WindowEnd_on_an_exact_boundary_does_not_return_the_instant_itself()
    {
        // A window that ended "now" would advertise max-age=0 and defeat the whole change.
        var exact = new DateTime(2026, 8, 16, 11, 0, 0, DateTimeKind.Utc);
        Assert.That(FileService.WindowEnd(exact), Is.GreaterThan(exact));
    }
}
