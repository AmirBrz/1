using System.Collections;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Samian.Application.Contracts;
using Samian.Domain.Events;

namespace Samian.Infrastructure.Messaging;

// Resolves IDomainEventHandler<TEvent> for an event whose COMPILE-TIME type is only IDomainEvent, so
// the generic interface has to be built at run time. That reflection cost is real, and it is paid once
// per raised event (not per handler lookup per request) — the price of keeping the Domain free of
// MediatR, which is not in the offline package cache and which the challenge does not require.
//
// An in-process, synchronous dispatcher is a deliberate simplification: the events still commit before
// they are announced, so swapping this class for a Redis/messaging outbox later changes one file and
// no handler. That is the point of the dispatcher being behind a port.
public sealed class DomainEventDispatcher : IDomainEventDispatcher
{
    private static readonly MethodInfo DispatchMethod =
        typeof(DomainEventDispatcher).GetMethod(nameof(NotifyHandlersAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly IServiceProvider _services;
    private readonly ILogger<DomainEventDispatcher> _logger;

    public DomainEventDispatcher(IServiceProvider services, ILogger<DomainEventDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        _services = services;
        _logger = logger;
    }

    public async Task DispatchAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);

        foreach (var domainEvent in domainEvents)
        {
            // Generic method instantiation avoids `dynamic`, whose binder is slower and whose failures
            // appear at run time with no stack frame that names the event type.
            var genericCall = DispatchMethod.MakeGenericMethod(domainEvent.GetType());

            try
            {
                await (Task)genericCall.Invoke(this, new object[] { domainEvent, cancellationToken })!;
            }
            catch (Exception exception)
            {
                // Dispatch itself must not fail the request: the write is already committed, so the
                // only honest answer to a broken subscriber is a loud log entry (and, in production, an
                // alert plus a retry queue — which is the outbox this class is designed to grow into).
                _logger.LogError(exception, "Failed to dispatch domain event {Event}.", domainEvent.GetType().Name);
            }
        }
    }

    private async Task NotifyHandlersAsync<TEvent>(TEvent domainEvent, CancellationToken cancellationToken)
        where TEvent : IDomainEvent
    {
        // RequestServices (scoped) is what makes handlers resolve the SAME DbContext as the use case:
        // a handler that loads an aggregate in a second scope can read a row that the first scope has
        // already changed but not yet committed.
        var handlers = _services.GetService<IEnumerable<IDomainEventHandler<TEvent>>>();

        if (handlers is null)
            return;

        foreach (var handler in handlers)
        {
            try
            {
                await handler.HandleAsync(domainEvent, cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Handler {Handler} failed for {Event}.", handler.GetType().Name, typeof(TEvent).Name);
            }
        }
    }
}
