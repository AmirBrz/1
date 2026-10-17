using Samian.Domain.Enums;
using Samian.Domain.ValueObjects;

namespace Samian.Domain.Events;

// All Order lifecycle events in one file: they are one feature of one aggregate, and subscribers
// read them together. Each event is an immutable record.
//
// The payload is deliberately DENORMALIZED (Total, LineCount, Number are copied in): a subscriber
// like an email or an invoice handler should not have to reload the aggregate to do its job —
// reloading would couple the subscriber back to the aggregate and reintroduce the ordering problem
// events were meant to remove.
public record OrderPlaced(
    Guid AggregateId,
    Guid CustomerId,
    DocumentNumber? Number,
    Money Total,
    int LineCount,
    DateTimeOffset OccurredOn) : IDomainEvent;

public record OrderConfirmed(
    Guid AggregateId,
    DocumentNumber? Number,
    Money Total,
    DateTimeOffset OccurredOn) : IDomainEvent;

public record OrderShipped(
    Guid AggregateId,
    DocumentNumber? Number,
    DateTimeOffset OccurredOn) : IDomainEvent;

// Raised by DeliverOrder(): "Delivered" is the business completion of the order, which is why
// the event is named after the domain fact and not after the status value.
public record OrderCompleted(
    Guid AggregateId,
    DocumentNumber? Number,
    Money Total,
    DateTimeOffset OccurredOn) : IDomainEvent;

// Reason travels with the event: cancelling is the one transition that always needs an explanation,
// and the audit trail lives outside the aggregate.
public record OrderCancelled(
    Guid AggregateId,
    DocumentNumber? Number,
    OrderStatus CancelledFrom,
    string? Reason,
    DateTimeOffset OccurredOn) : IDomainEvent;


// Discount carries the resulting total discount and Total the amount the customer now owes: every
// other lifecycle event ships the resulting Total, and an invoice/CRM subscriber that had to recompute
// it would be coupling itself back to the aggregate.
// Recorded decision: Discount is the NEW TOTAL discount on the document (replacement semantics), not
// the delta of this call — Order.ApplyDiscount overwrites rather than accumulates.
public record OrderDiscountApplied(
    Guid AggregateId,
    DocumentNumber? Number,
    Money Discount,
    Money Total,
    DateTimeOffset OccurredOn) : IDomainEvent;
