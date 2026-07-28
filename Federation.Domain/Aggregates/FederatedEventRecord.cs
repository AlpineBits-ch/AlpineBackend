namespace Federation.Domain.Aggregates;

public class FederatedEventRecord
{
    public string EventId { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string ScopeKey { get; set; } = string.Empty;
    public long Depth { get; set; }
    public string[] PreviousEventIds { get; set; } = [];
    public string PayloadJson { get; set; } = string.Empty;
    public bool Applied { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>
    /// Null for inbound records. For outbound records, the resolved destination host the event
    /// was (or still needs to be) POSTed to - previously nothing tracked this, so a failed POST
    /// was silently dropped forever even though StampAndRecordAsync had already advanced the
    /// local DAG tip, permanently orphaning that branch on the remote side.
    /// </summary>
    public string? TargetHost { get; set; }

    /// <summary>Outbound only. True once a POST to <see cref="TargetHost"/> has succeeded.</summary>
    public bool Delivered { get; set; } = true;

    /// <summary>Outbound only. Incremented on each failed delivery attempt.</summary>
    public int Attempts { get; set; }
}
