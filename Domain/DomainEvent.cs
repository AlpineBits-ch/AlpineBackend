namespace Domain;

public abstract class DomainEvent
{
    public string CorrelationId { get; set; }
}