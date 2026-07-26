using Isle.Domain.Aggregates;
using Isle.Domain.Enums;

namespace Isle.Tests.Tests.Domain;

[TestFixture]
public class QuestInstanceTests
{
    private static QuestInstance Spawn(TimeSpan? duration = null) => QuestInstance.Spawn(new SpawnQuestInstanceArgs
    {
        QuestId = "quest_1",
        Title = "Test Quest",
        Duration = duration ?? TimeSpan.FromMinutes(30),
    });

    [Test]
    public void Spawn_SetsActiveStateAndExpiry()
    {
        var before = DateTimeOffset.UtcNow;
        var instance = Spawn(TimeSpan.FromMinutes(30));

        Assert.That(instance.State, Is.EqualTo(QuestInstanceState.Active));
        Assert.That(instance.IsOpen, Is.True);
        Assert.That(instance.ExpiresAt, Is.EqualTo(instance.StartedAt.AddMinutes(30)));
        Assert.That(instance.StartedAt, Is.GreaterThanOrEqualTo(before));
    }

    [Test]
    public void TryClose_FromActive_Succeeds()
    {
        var instance = Spawn();

        var closed = instance.TryClose(QuestInstanceState.Completed, "player_1");

        Assert.That(closed, Is.True);
        Assert.That(instance.State, Is.EqualTo(QuestInstanceState.Completed));
        Assert.That(instance.CompletedByPlayerId, Is.EqualTo("player_1"));
        Assert.That(instance.EndedAt, Is.Not.Null);
        Assert.That(instance.IsOpen, Is.False);
    }

    [Test]
    public void TryClose_AlreadyClosed_IsANoOpAndReturnsFalse()
    {
        var instance = Spawn();
        instance.TryClose(QuestInstanceState.Completed, "player_1");
        var endedAtFirstClose = instance.EndedAt;

        var secondClose = instance.TryClose(QuestInstanceState.Expired, "player_2");

        Assert.That(secondClose, Is.False);
        Assert.That(instance.State, Is.EqualTo(QuestInstanceState.Completed), "the first close wins");
        Assert.That(instance.CompletedByPlayerId, Is.EqualTo("player_1"));
        Assert.That(instance.EndedAt, Is.EqualTo(endedAtFirstClose));
    }

    [Test]
    public void TryClose_ToActive_IsRejected()
    {
        var instance = Spawn();

        var closed = instance.TryClose(QuestInstanceState.Active);

        Assert.That(closed, Is.False);
        Assert.That(instance.State, Is.EqualTo(QuestInstanceState.Active));
    }

    [Test]
    public void HasExpired_FalseBeforeExpiry()
    {
        var instance = Spawn(TimeSpan.FromMinutes(30));

        Assert.That(instance.HasExpired(DateTimeOffset.UtcNow), Is.False);
    }

    [Test]
    public void HasExpired_TrueAfterExpiry()
    {
        var instance = Spawn(TimeSpan.FromMinutes(30));

        Assert.That(instance.HasExpired(DateTimeOffset.UtcNow.AddMinutes(31)), Is.True);
    }

    [Test]
    public void HasExpired_FalseOnceClosed_EvenPastExpiry()
    {
        var instance = Spawn(TimeSpan.FromMinutes(30));
        instance.TryClose(QuestInstanceState.Completed);

        Assert.That(instance.HasExpired(DateTimeOffset.UtcNow.AddMinutes(31)), Is.False);
    }

    [Test]
    public void FriendlyId_RoundTripsThroughDecode()
    {
        var instance = Spawn();
        instance.FriendlyIdSeq = 1234;

        var decoded = QuestInstance.DecodeFriendlyId(instance.FriendlyId);

        Assert.That(decoded, Is.EqualTo(1234));
    }

    [Test]
    public void DecodeFriendlyId_AcceptsLowercaseAndMissingPrefix()
    {
        var instance = Spawn();
        instance.FriendlyIdSeq = 5000;
        var canonical = instance.FriendlyId; // "Q-xxxxx"
        var messy = canonical.Replace("Q-", "").ToLowerInvariant();

        Assert.That(QuestInstance.DecodeFriendlyId(messy), Is.EqualTo(5000));
    }

    [Test]
    public void DecodeFriendlyId_RejectsArbitraryStrings()
    {
        Assert.That(QuestInstance.DecodeFriendlyId("not-a-real-id"), Is.Null);
        Assert.That(QuestInstance.DecodeFriendlyId(null), Is.Null);
        Assert.That(QuestInstance.DecodeFriendlyId(""), Is.Null);
    }

    [Test]
    public void Spawn_ZeroDuration_IsImmediatelyExpired()
    {
        var instance = Spawn(TimeSpan.Zero);

        Assert.That(instance.ExpiresAt, Is.EqualTo(instance.StartedAt));
        Assert.That(instance.HasExpired(DateTimeOffset.UtcNow), Is.True);
    }

    [Test]
    public void HasExpired_TrueExactlyAtTheExpiryBoundary()
    {
        var instance = Spawn(TimeSpan.FromMinutes(30));

        // HasExpired uses >=, so the exact expiry instant must already read as expired.
        Assert.That(instance.HasExpired(instance.ExpiresAt), Is.True);
    }
}
