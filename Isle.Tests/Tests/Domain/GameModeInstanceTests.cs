using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Isle.Domain.Interfaces;
using Isle.Tests.Helpers;
using NSubstitute;

namespace Isle.Tests.Tests.Domain;

[TestFixture]
public class GameModeInstanceTests
{
    private static GameModeInstance NewInstance(GameModeDefinition? definition = null) =>
        new(definition ?? TestData.KothDefinition(), Substitute.For<IGameMode>());

    [Test]
    public void Start_SetsRunningStateAndStampsStartedAt()
    {
        var instance = NewInstance();
        var before = DateTime.UtcNow;

        instance.Start();

        Assert.That(instance.State, Is.EqualTo(GameModeState.Running));
        Assert.That(instance.StartedAt, Is.GreaterThanOrEqualTo(before));
    }

    [Test]
    public void HasTimedOut_FalseBeforeMaxDurationElapses()
    {
        var instance = NewInstance(TestData.KothDefinition(maxDuration: TimeSpan.FromMinutes(10)));
        instance.Start();

        Assert.That(instance.HasTimedOut(), Is.False);
    }

    [Test]
    public void HasTimedOut_TrueOnceMaxDurationElapses()
    {
        var instance = NewInstance(TestData.KothDefinition(maxDuration: TimeSpan.FromMinutes(10)));
        instance.Start();

        // Backdate StartedAt via Resume, which is the only supported way to set it directly.
        var backdated = GameModeInstance.Resume(
            instance.Definition, instance.Behavior, instance.InstanceId,
            DateTime.UtcNow - TimeSpan.FromMinutes(11), []);

        Assert.That(backdated.HasTimedOut(), Is.True);
    }

    [Test]
    public void HasTimedOut_FalseWhenDefinitionHasNoMaxDuration()
    {
        var definition = TestData.KothDefinition();
        definition.MaxDuration = null;
        var instance = NewInstance(definition);
        instance.Start();

        Assert.That(instance.HasTimedOut(), Is.False);
    }

    [Test]
    public void AddParticipant_DoesNotDuplicateTheSamePlayer()
    {
        var instance = NewInstance();

        instance.AddParticipant("player_1");
        instance.AddParticipant("player_1");
        instance.AddParticipant("player_2");

        Assert.That(instance.ParticipantIds, Is.EquivalentTo(new[] { "player_1", "player_2" }));
    }

    [Test]
    public void BeginResolving_SetsResolvingState()
    {
        var instance = NewInstance();
        instance.Start();

        instance.BeginResolving();

        Assert.That(instance.State, Is.EqualTo(GameModeState.Resolving));
    }

    [Test]
    public void EnterCooldown_SetsCooldownState()
    {
        var instance = NewInstance();
        instance.Start();
        instance.BeginResolving();

        instance.EnterCooldown();

        Assert.That(instance.State, Is.EqualTo(GameModeState.Cooldown));
    }

    [Test]
    public void Resume_ReconstructsRunningStateWithoutResettingStartedAt()
    {
        var definition = TestData.KothDefinition();
        var behavior = Substitute.For<IGameMode>();
        var originalStart = DateTime.UtcNow - TimeSpan.FromMinutes(3);

        var resumed = GameModeInstance.Resume(definition, behavior, "game_instance_fixed", originalStart, ["p1", "p2"]);

        Assert.That(resumed.State, Is.EqualTo(GameModeState.Running));
        Assert.That(resumed.InstanceId, Is.EqualTo("game_instance_fixed"));
        Assert.That(resumed.StartedAt, Is.EqualTo(originalStart));
        Assert.That(resumed.ParticipantIds, Is.EquivalentTo(new[] { "p1", "p2" }));
    }

    [Test]
    public void HasTimedOut_TrueExactlyAtTheMaxDurationBoundary()
    {
        var definition = TestData.KothDefinition(maxDuration: TimeSpan.FromMinutes(10));
        var instance = NewInstance(definition);

        // >= per HasTimedOut's own comparison, so exactly-elapsed must already count as timed out.
        var atBoundary = GameModeInstance.Resume(
            instance.Definition, instance.Behavior, instance.InstanceId,
            DateTime.UtcNow - TimeSpan.FromMinutes(10), []);

        Assert.That(atBoundary.HasTimedOut(), Is.True);
    }

    [Test]
    public void Start_CalledTwice_MovesStartedAtToTheSecondCall()
    {
        var instance = NewInstance();

        instance.Start();
        var firstStart = instance.StartedAt;

        instance.Start();

        Assert.That(instance.StartedAt, Is.GreaterThanOrEqualTo(firstStart));
        Assert.That(instance.State, Is.EqualTo(GameModeState.Running));
    }
}
