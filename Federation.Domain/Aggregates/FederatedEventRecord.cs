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
}
