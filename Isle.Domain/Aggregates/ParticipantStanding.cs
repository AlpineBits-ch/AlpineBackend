namespace Isle.Domain.Aggregates;

public class ParticipantStanding
{  
    public string PlayerId { get; init; }
    public int Score { get; init; }
    public int Rank { get; init; }
    public Dictionary<string, object> CustomMetrics { get; init; } 
}