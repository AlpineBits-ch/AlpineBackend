namespace Identity.Domain.Enums;

/// <summary>Where a data-subject request is in the queue.</summary>
public enum DataSubjectRequestStatus
{
    /// <summary>Received, nobody has picked it up.</summary>
    Open,

    /// <summary>Someone is working it. Still on the clock.</summary>
    InProgress,

    /// <summary>Answered, with a <see cref="DataSubjectRequestDisposition"/>. The clock stops.</summary>
    Closed,
}
