// Gives you one place to raise OrderPlaced without the entity knowing who consumes it.
using Samian.Domain.Events;

namespace Samian.Domain.Common;

// Marker: "this entity is a consistency boundary". Repositories may only take roots of this kind.
public interface IAggregateRoot<TId> : IEntity<TId>
{
}

public abstract class AggregateRoot : Entity<Guid>, IAggregateRoot<Guid>
{
    private readonly List<IDomainEvent> _domainEvents = new();

    // Read-only view: outsiders can see the events, never add or clear them.
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    // protected: raising an event is an act of business behaviour, so only the aggregate itself does it.
    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    // Public by necessity: Infrastructure calls this right AFTER SaveChanges, once the events have been
    // dispatched. Nothing else should ever call it — that's the one leak in an otherwise pure domain.
    public void ClearDomainEvents() => _domainEvents.Clear();
}
