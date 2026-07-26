using Domain;
using Isle.Domain.Enums;
using Isle.Domain.Interfaces;
using Persistence;

namespace Isle.Domain.Aggregates;

public class GameModeInstance : Aggregate<GameModeInstance>, IPrefixedEntity
{
    public string InstanceId { get; private set; } = GenerateId();
    public GameModeDefinition Definition { get; }
    public IGameMode Behavior { get; }
    public GameModeState State { get; internal set; } = GameModeState.Idle;
    public DateTime StartedAt { get; private set; }

    private readonly List<string> _participantIds = new();
    public IReadOnlyList<string> ParticipantIds => _participantIds;

    public GameModeInstance(GameModeDefinition definition, IGameMode behavior)
    {
        Definition = definition;
        Behavior = behavior;
    }

    public void Start()
    {
        StartedAt = DateTime.UtcNow;
        State = GameModeState.Running;
    }

    public void AddParticipant(string playerId)
    {
        if (!_participantIds.Contains(playerId))
            _participantIds.Add(playerId);
    }

    public bool HasTimedOut()
    {
        return Definition.MaxDuration is { } max &&
               DateTime.UtcNow - StartedAt >= max;
    }

    /// <summary>Match is over, standings/rewards are being worked out.</summary>
    public void BeginResolving() => State = GameModeState.Resolving;

    /// <summary>Resolution finished; nothing may trigger again until <c>Definition.Cooldown</c> elapses.</summary>
    public void EnterCooldown() => State = GameModeState.Cooldown;

    /// <summary>
    /// Reconstructs a match that was already running when the process last stopped, from the durable
    /// marker in <c>KingOfTheHillMatchStateStore</c> (or any future mode's equivalent). Sets state
    /// directly instead of calling <see cref="Start"/>, which would reset <see cref="StartedAt"/> to now
    /// and re-fire "just started" semantics for a match that has been live the whole time.
    /// </summary>
    public static GameModeInstance Resume(
        GameModeDefinition definition,
        IGameMode behavior,
        string instanceId,
        DateTime startedAt,
        IEnumerable<string> participantIds)
    {
        var instance = new GameModeInstance(definition, behavior)
        {
            InstanceId = instanceId,
            StartedAt = startedAt,
            State = GameModeState.Running,
        };

        foreach (var playerId in participantIds)
            instance.AddParticipant(playerId);

        return instance;
    }

    public static string Prefix { get; } = "game_instance";
}