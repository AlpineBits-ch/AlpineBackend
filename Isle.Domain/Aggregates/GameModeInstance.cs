using Domain;
using Isle.Domain.Enums;
using Isle.Domain.Interfaces;
using Persistence;

namespace Isle.Domain.Aggregates;

public class GameModeInstance : Aggregate<GameModeInstance>, IPrefixedEntity
{
    public string InstanceId { get; } = GenerateId();
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

    public static string Prefix { get; } = "game_instance";
    
    
    
}