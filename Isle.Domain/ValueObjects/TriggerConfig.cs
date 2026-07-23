using Isle.Domain.Enums;

namespace Isle.Domain.ValueObjects;

public class TriggerConfig
{
    public TriggerType Type { get; set; }

    // Timer-based
    public TimeSpan? Interval { get; set; }

    // Zone-entry-based
    public int? MinPlayersToTrigger { get; set; }

    // Admin-triggered needs no extra fields — presence of the command is enough
}

