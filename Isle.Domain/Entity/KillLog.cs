using Isle.Domain.Aggregates;
using Persistence;

namespace Isle.Domain.Entity;

public class KillLog : BaseEntity<KillLog>, IPrefixedEntity
{
    public string? KillerId { get; set; }
    public virtual Player? Killer { get; set; }
    public string? VictimId { get; set; }
    public virtual Player? Victim { get; set; }
    public double VictimWeightKg { get; set; }
    public static string Prefix { get; } = "Kill_log";
}