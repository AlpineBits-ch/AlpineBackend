namespace Isle.Contracts.Events.Player;

public class PlayerKillEvent
{
    public string KilerId { get; set; }
    public string VictimId { get; set; }
    public double VictimWeightInKg { get; set; }
}