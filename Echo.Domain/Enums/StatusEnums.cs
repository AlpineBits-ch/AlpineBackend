namespace Echo.Domain.Enums;

/// <summary>Where an incident record came from.</summary>
public enum IncidentOrigin
{
    Manual,
    Automatic,
}

/// <summary>Whether a record is an outage or a planned window.</summary>
public enum IncidentKind
{
    Incident,
    Maintenance,
}

/// <summary>
/// The lifecycle, and the four words the public page prints beside every timeline entry.
/// </summary>
public enum IncidentStatus
{
    Investigating,
    Identified,
    Monitoring,
    Resolved,

    // Maintenance only.
    Scheduled,
    InProgress,
    Completed,
    Cancelled,
}

/// <summary>How much of a problem this is, as the page colours it.</summary>
public enum IncidentImpact
{
    /// <summary>Used by maintenance that changes nothing a user would notice, and by incidents
    /// downgraded after the fact rather than deleted.</summary>
    None,

    Minor,
    Major,
    Critical,
}

/// <summary>A component's current state.</summary>
public enum ComponentStatus
{
    Operational,

    /// <summary>Some requests are failing.</summary>
    DegradedPerformance,

    PartialOutage,
    MajorOutage,
    UnderMaintenance,
}

/// <summary>
/// The one-word answer at the top of the page, derived from the worst component and never stored.
/// </summary>
public enum StatusIndicator
{
    Operational,
    Degraded,
    PartialOutage,
    MajorOutage,
    Maintenance,
}
