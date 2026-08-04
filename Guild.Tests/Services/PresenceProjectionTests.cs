using Guild.Application.Services;
using OnlineStatus = Guild.Application.Dtos.Response.OnlineStatus;

namespace Guild.Tests.Services;

/// <summary>
/// Covers <see cref="PresenceProjection"/> - the single rule behind privacy spec T0-5.
/// </summary>
[TestFixture]
public class PresenceProjectionTests
{
    // ── Normal ────────────────────────────────────────────────────────────────

    [Test]
    public void ProjectFor_HiddenSeenByAThirdParty_RendersAsOffline()
    {
        Assert.That(
            PresenceProjection.ProjectFor(OnlineStatus.Hidden, viewerIsSubject: false),
            Is.EqualTo(OnlineStatus.Offline));
    }

    [Test]
    public void ProjectFor_HiddenSeenByTheUserThemselves_StaysHidden()
    {
        // Without this the picker in the user's own client cannot render what they chose.
        Assert.That(
            PresenceProjection.ProjectFor(OnlineStatus.Hidden, viewerIsSubject: true),
            Is.EqualTo(OnlineStatus.Hidden));
    }

    [TestCase(OnlineStatus.Online)]
    [TestCase(OnlineStatus.Idle)]
    [TestCase(OnlineStatus.DoNotDisturb)]
    [TestCase(OnlineStatus.Offline)]
    public void ProjectFor_EveryOtherStatus_IsUntouchedForEitherViewer(OnlineStatus status)
    {
        Assert.Multiple(() =>
        {
            Assert.That(PresenceProjection.ProjectFor(status, viewerIsSubject: false), Is.EqualTo(status));
            Assert.That(PresenceProjection.ProjectFor(status, viewerIsSubject: true), Is.EqualTo(status));
        });
    }

    [Test]
    public void ProjectNameFor_RoundTripsNamesForTheRealtimePath()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PresenceProjection.ProjectNameFor("Online", viewerIsSubject: false), Is.EqualTo("Online"));
            Assert.That(PresenceProjection.ProjectNameFor("Hidden", viewerIsSubject: false), Is.EqualTo("Offline"));
            Assert.That(PresenceProjection.ProjectNameFor("Hidden", viewerIsSubject: true), Is.EqualTo("Hidden"));
        });
    }

    // ── Edge ──────────────────────────────────────────────────────────────────

    [Test]
    public void TryParse_IsCaseInsensitive_BecauseTheValueComesFromAnotherServiceOverRedis()
    {
        Assert.That(PresenceProjection.TryParse("hidden", out var status), Is.True);
        Assert.That(status, Is.EqualTo(OnlineStatus.Hidden));
    }

    [Test]
    public void ProjectNameFor_LowercasedHidden_StillProjectsToOffline()
    {
        // A leak that only needs the writer to differ in casing would be the same leak.
        Assert.That(PresenceProjection.ProjectNameFor("hidden", viewerIsSubject: false), Is.EqualTo("Offline"));
    }

    // ── Negative ──────────────────────────────────────────────────────────────

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("Invisible")]
    public void TryParse_UnrecognisedOrAbsent_FailsAndFallsBackToOffline(string? raw)
    {
        Assert.That(PresenceProjection.TryParse(raw, out var status), Is.False);
        Assert.That(status, Is.EqualTo(OnlineStatus.Offline));
    }

    [Test]
    public void TryParse_NumericString_IsRejected()
    {
        // Enum.TryParse accepts "1" and would silently produce Hidden. A stored status is a name.
        Assert.That(PresenceProjection.TryParse("1", out var status), Is.False);
        Assert.That(status, Is.EqualTo(OnlineStatus.Offline));
    }

    [Test]
    public void ProjectNameFor_UnknownStatus_DoesNotPassItThrough()
    {
        // The value a future release adds must not reach a peer just because this one cannot name
        // it - that is how Hidden leaked in the first place.
        Assert.That(PresenceProjection.ProjectNameFor("SomethingNew", viewerIsSubject: false), Is.EqualTo("Offline"));
    }
}
