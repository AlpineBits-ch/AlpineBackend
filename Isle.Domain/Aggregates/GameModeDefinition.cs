using Isle.Domain.ValueObjects;

namespace Isle.Domain.Aggregates;

public class GameModeDefinition
{
    public Guid Id { get; set; }
    public string Type { get; set; }              // e.g. "KingOfTheHill" — resolves to an IGameMode via factory
    public string DisplayName { get; set; }
    public bool Enabled { get; set; }

    public GeoFenceData Zone { get; set; }
    public TriggerConfig Trigger { get; set; }

    public TimeSpan? MaxDuration { get; set; }     // null = runs until manually ended
    public TimeSpan Cooldown { get; set; }         // time before this definition can trigger again
    public int MinParticipants { get; set; } = 1;
    public int? MaxParticipants { get; set; }      // null = unlimited

    public List<RewardConfig> Rewards { get; set; } = new();

    public DateTime CreatedAt { get; set; }
    public DateTime? LastRunAt { get; set; }   
}