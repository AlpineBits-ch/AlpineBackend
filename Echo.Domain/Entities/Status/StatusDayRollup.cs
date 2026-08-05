using Echo.Domain.Enums;
using Persistence;

namespace Echo.Domain.Entities.Status;

/// <summary>One component, one UTC day, as seconds spent in each state.</summary>
public class StatusDayRollup : BaseEntity<StatusDayRollup>, IPrefixedEntity
{
    public static string Prefix => "stdr";

    /// <summary>How long a day of history is kept.</summary>
    public const int RetentionDays = 90;

    public string ComponentId { get; set; } = null!;
    public StatusComponent? Component { get; set; }

    /// <summary>Midnight UTC of the day this row covers.</summary>
    public DateOnly Day { get; set; }

    public double OperationalSeconds { get; set; }
    public double DegradedSeconds { get; set; }
    public double OutageSeconds { get; set; }
    public double MaintenanceSeconds { get; set; }

    public int IncidentCount { get; set; }

    public double AccountedSeconds =>
        OperationalSeconds + DegradedSeconds + OutageSeconds + MaintenanceSeconds;

    /// <summary>The day's uptime as a fraction, or null if nothing was recorded.</summary>
    public double? Uptime
    {
        get
        {
            var total = AccountedSeconds;
            if (total <= 0) return null;

            return (OperationalSeconds + MaintenanceSeconds + (DegradedSeconds * 0.5)) / total;
        }
    }

    public static StatusDayRollup Create(string componentId, DateOnly day, DateTimeOffset now) => new()
    {
        Id = GenerateId(),
        CreatedAt = now,
        UpdatedAt = now,
        ComponentId = componentId,
        Day = day,
    };

    /// <summary>Adds an interval to whichever bucket the component was in for it.</summary>
    public void Add(ComponentStatus status, double seconds, DateTimeOffset now)
    {
        switch (status)
        {
            case ComponentStatus.Operational:
                OperationalSeconds += seconds;
                break;
            case ComponentStatus.DegradedPerformance:
                DegradedSeconds += seconds;
                break;
            case ComponentStatus.PartialOutage:
            case ComponentStatus.MajorOutage:
                OutageSeconds += seconds;
                break;
            case ComponentStatus.UnderMaintenance:
                MaintenanceSeconds += seconds;
                break;
        }

        UpdatedAt = now;
    }
}
