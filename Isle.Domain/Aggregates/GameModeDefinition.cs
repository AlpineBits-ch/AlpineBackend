using Isle.Domain.Entity;
using Isle.Domain.Enums;
using Isle.Domain.ValueObjects;
using Persistence;

namespace Isle.Domain.Aggregates;

public class GameModeDefinition : BaseEntity<GameModeDefinition>, IPrefixedEntity
{
    public GameModeType Type { get; set; }  
    public string DisplayName { get; set; }
    public bool Enabled { get; set; }

    public GeoFenceData Zone { get; set; }
    public TriggerConfig Trigger { get; set; }

    public TimeSpan? MaxDuration { get; set; }
    public TimeSpan Cooldown { get; set; }
    public int MinParticipants { get; set; } = 1;
    public int? MaxParticipants { get; set; }

    public List<RewardConfig> Rewards { get; set; } = new();

    public DateTime CreatedAt { get; set; }
    public DateTime? LastRunAt { get; set; }
    public static string Prefix { get; } = "game_definition";
    
    public virtual ICollection<GameModeRun> Runs { get; set; } = new List<GameModeRun>();
}