using Samian.Domain.Events;

namespace Samian.Application.Contracts;

// A SUBSCRIBER to a domain fact. `in TEvent` (contravariant) is what lets one handler class implement
// IDomainEventHandler<OrderPlaced> and IDomainEventHandler<OrderConfirmed> at once, and it lets the
// dispatcher ask for handlers of an event it only knows as `IDomainEvent`.
//
// Handlers run AFTER the save succeeded (see IUnitOfWork in the Domain): a fact that never reached
// the database must not be announced. They must be idempotent and must never throw the request down
// — Infrastructure's dispatcher catches and logs per handler.
public interface IDomainEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken = default);
}

// The pipeline that finds the handlers and runs them. Owned by Application because orchestration order
// is an application concern; implemented by Infrastructure because it needs the service provider.
public interface IDomainEventDispatcher
{
    Task DispatchAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken = default);
}
