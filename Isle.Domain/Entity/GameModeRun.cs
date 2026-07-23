using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Persistence;

namespace Isle.Domain.Entity;

public class GameModeRun : BaseEntity<GameModeRun>, IPrefixedEntity
{
    public string? DefinitionId { get; private set; }
    public virtual GameModeDefinition? Definition { get; private set; }
    public GameModeType GameModeType { get; private set; }
    public DateTime StartedAt { get; private set; }
    public DateTime EndedAt { get; private set; }
    public List<ParticipantResult> Results { get; private set; } = new();

    private GameModeRun() { } // EF

    public GameModeRun(GameModeInstance instance, IReadOnlyList<ParticipantStanding> finalStandings)
    {
        Id = GenerateId();
        DefinitionId = instance.Definition.Id;
        GameModeType = instance.Definition.Type;
        StartedAt = instance.StartedAt;
        EndedAt = DateTime.UtcNow;
        Results = finalStandings
            .Select(s => new ParticipantResult(s.PlayerId, s.Score, s.Rank))
            .ToList();
    }

    public static string Prefix { get; } = "run";
}

public record ParticipantResult(string PlayerId, int Score, int Rank);