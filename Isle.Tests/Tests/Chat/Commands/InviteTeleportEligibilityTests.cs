using Isle.Api.Chat.Commands;
using Isle.Domain.Aggregates;

namespace Isle.Tests.Tests.Chat.Commands;

[TestFixture]
public class InviteTeleportEligibilityTests
{
    [Test]
    public void Check_FreshSpawnLowGrowth_IsEligible()
    {
        var (ok, error) = InviteTeleportEligibility.Check(0.1, DateTimeOffset.UtcNow);

        Assert.That(ok, Is.True);
        Assert.That(error, Is.Null);
    }

    [Test]
    public void Check_GrowthAtMax_IsEligible()
    {
        var (ok, error) = InviteTeleportEligibility.Check(PlayerInvite.MaxGrowthForTeleport, DateTimeOffset.UtcNow);

        Assert.That(ok, Is.True);
        Assert.That(error, Is.Null);
    }

    [Test]
    public void Check_GrowthAboveMax_IsNotEligible()
    {
        var (ok, error) = InviteTeleportEligibility.Check(PlayerInvite.MaxGrowthForTeleport + 0.01, DateTimeOffset.UtcNow);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("fresh spawns only"));
    }

    [Test]
    public void Check_NullLastSpawn_IsNotEligible()
    {
        var (ok, error) = InviteTeleportEligibility.Check(0.1, null);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("minutes of spawning"));
    }

    [Test]
    public void Check_LastSpawnOutsideWindow_IsNotEligible()
    {
        var lastSpawn = DateTimeOffset.UtcNow - PlayerInvite.SpawnWindow - TimeSpan.FromSeconds(1);

        var (ok, error) = InviteTeleportEligibility.Check(0.1, lastSpawn);

        Assert.That(ok, Is.False);
        Assert.That(error, Does.Contain("minutes of spawning"));
    }

    [Test]
    public void Check_LastSpawnJustInsideWindow_IsEligible()
    {
        var lastSpawn = DateTimeOffset.UtcNow - PlayerInvite.SpawnWindow + TimeSpan.FromSeconds(5);

        var (ok, _) = InviteTeleportEligibility.Check(0.1, lastSpawn);

        Assert.That(ok, Is.True);
    }
}
