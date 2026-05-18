using System.ComponentModel.DataAnnotations.Schema;
using Persistence;

namespace Domain;

public interface IEventSource
{
    public IReadOnlyCollection<DomainEvent> GetDomainEvents();
    protected virtual void AddDomainEvent(DomainEvent domainEvent)
    {
    }
}

public abstract class Aggregate<T> :  BaseEntity<T>, IEventSource where T : Aggregate<T>, IPrefixedEntity
{
    [NotMapped]
    private readonly object _lock = new();

    [NotMapped]
    private readonly List<DomainEvent> _domainEvents = new();
    private IReadOnlyCollection<DomainEvent> DomainEvents => _domainEvents.ToList().AsReadOnly();
    

    /// <summary>Add a domain event that will be published to the event bus</summary>
    protected virtual void AddDomainEvent(DomainEvent domainEvent)
    {
        lock (_lock)
        {
            domainEvent.CorrelationId = this.Id;
            _domainEvents.Add(domainEvent);
        }
    }

    public void ClearDomainEvents() => _domainEvents.Clear();

    public bool HasDomainEvents() => _domainEvents.Any();

    public IReadOnlyCollection<DomainEvent> GetDomainEvents() => DomainEvents;
}