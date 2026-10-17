using Microsoft.Extensions.Logging;
using Samian.Application.Contracts;
using Samian.Domain.Contracts;
using Samian.Domain.Enums;
using Samian.Domain.Events;

namespace Samian.Application.Events;

// These classes are the reason domain events exist. The aggregate announced a FACT; deciding what to do
// about it (mail, audit, integration) lives here, so Order never mentions e-mail and adding a WhatsApp
// notification later means ONE new handler plus one DI line — no change to the aggregate, no change to
// the use case, no change to the other handlers (Open/Closed, stated concretely).
//
// What a handler is allowed to read: another aggregate (Customer) for ADDRESSING. What it must never do:
// reload the aggregate that raised the event to get data the event should have carried — that is the
// coupling the denormalized payloads in OrderEvents.cs exist to prevent.

public sealed class NotifyCustomerOnOrderPlacedHandler : IDomainEventHandler<OrderPlaced>
{
    private readonly IEmailSender _email;
    private readonly ICustomerRepository _customers;
    private readonly ILogger<NotifyCustomerOnOrderPlacedHandler> _logger;

    public NotifyCustomerOnOrderPlacedHandler(
        IEmailSender email,
        ICustomerRepository customers,
        ILogger<NotifyCustomerOnOrderPlacedHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(customers);
        ArgumentNullException.ThrowIfNull(logger);

        _email = email;
        _customers = customers;
        _logger = logger;
    }

    public async Task HandleAsync(OrderPlaced domainEvent, CancellationToken cancellationToken = default)
    {
        // The event carries CustomerId, so this is ONE lookup of a DIFFERENT aggregate (for an address)
        // and zero lookups of the aggregate that raised the event. That asymmetry is the rule.
        var customer = await _customers.GetByIdAsync(domainEvent.CustomerId, cancellationToken);

        // No address of record is a normal, not exceptional, state for a sample customer — so this is a
        // logged skip, not a throw. A handler that throws on missing optional data turns a successful
        // order into a failed request, which is the opposite of what an event is for.
        if (customer?.Email is null)
        {
            _logger.LogWarning(
                "Order {Number} was placed but customer {CustomerId} has no e-mail on file, so no confirmation was sent.",
                domainEvent.Number?.Value ?? "(unnumbered)",
                domainEvent.CustomerId);

            return;
        }

        var subject = $"Order {domainEvent.Number?.Value} confirmed";
        var body =
            $"Hello {customer.Name},\n\n" +
            $"Your order {domainEvent.Number?.Value} was placed on {domainEvent.OccurredOn:u} with " +
            $"{domainEvent.LineCount} line(s), totalling {domainEvent.Total.Amount} {domainEvent.Total.Currency}.\n\n" +
            "Thank you for your business.";

        await _email.SendAsync(customer.Email, subject, body, cancellationToken);
    }
}

// Cancellation is announced, not acted upon, in this sample: the restock decision ("does a cancelled
// shipment put units back on the shelf?") is a business rule the domain has not agreed to yet, so the
// honest handler records the fact and waits. Writing `product.IncreaseStock(...)` here would be
// inventing policy inside an adapter — the place where nobody reviews it.
public sealed class RecordOrderCancellationHandler : IDomainEventHandler<OrderCancelled>
{
    private readonly ILogger<RecordOrderCancellationHandler> _logger;

    public RecordOrderCancellationHandler(ILogger<RecordOrderCancellationHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
    }

    public Task HandleAsync(OrderCancelled domainEvent, CancellationToken cancellationToken = default)
    {
        // The event says WHICH state it was cancelled from, so the record can be accurate about whether
        // anybody has to do something (a cancelled draft needs no follow-up, a cancelled confirmation does).
        var requiresAction = domainEvent.CancelledFrom is OrderStatus.Placed or OrderStatus.Confirmed;

        _logger.LogInformation(
            "Order {Number} cancelled from {FromState} ({Reason}). Follow-up required: {Action}.",
            domainEvent.Number?.Value ?? "(unnumbered)",
            domainEvent.CancelledFrom,
            domainEvent.Reason ?? "not specified",
            requiresAction);

        return Task.CompletedTask;
    }
}

// ONE class, three facts: a handler may subscribe to as many events as it needs, because
// IDomainEventHandler<in TEvent> is contravariant in the event type and each subscription is a separate
// interface implementation the dispatcher can resolve on its own.
public sealed class AuditOrderLifecycleHandler :
    IDomainEventHandler<OrderPlaced>,
    IDomainEventHandler<OrderShipped>,
    IDomainEventHandler<OrderCompleted>
{
    private readonly ILogger<AuditOrderLifecycleHandler> _logger;

    public AuditOrderLifecycleHandler(ILogger<AuditOrderLifecycleHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
    }

    public Task HandleAsync(OrderPlaced domainEvent, CancellationToken cancellationToken = default)
    {
        // Audit lives HERE, not in the aggregate: Order.CancelOrder's comment says "the audit trail lives
        // outside the aggregate", and this is that outside. The aggregate only records business state.
        _logger.LogInformation(
            "AUDIT | order {Number} placed | total {Total} {Currency} | lines {Lines} | at {At:u}",
            domainEvent.Number?.Value ?? "(unnumbered)",
            domainEvent.Total.Amount,
            domainEvent.Total.Currency,
            domainEvent.LineCount,
            domainEvent.OccurredOn);

        return Task.CompletedTask;
    }

    public Task HandleAsync(OrderShipped domainEvent, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "AUDIT | order {Number} shipped at {At:u}",
            domainEvent.Number?.Value ?? "(unnumbered)",
            domainEvent.OccurredOn);

        return Task.CompletedTask;
    }

    public Task HandleAsync(OrderCompleted domainEvent, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "AUDIT | order {Number} delivered | total {Total} {Currency} | at {At:u}",
            domainEvent.Number?.Value ?? "(unnumbered)",
            domainEvent.Total.Amount,
            domainEvent.Total.Currency,
            domainEvent.OccurredOn);

        return Task.CompletedTask;
    }
}
