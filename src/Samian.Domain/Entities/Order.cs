using Samian.Domain.Common;
using Samian.Domain.Enums;
using Samian.Domain.Events;
using Samian.Domain.Exceptions;
using Samian.Domain.ValueObjects;

namespace Samian.Domain.Entities;

// Order is the aggregate root: it owns the OrderLine children, it is the only way to change them,
// and it is the only thing a repository loads or saves.
public class Order : AggregateRoot
{
    // EF Core materializes entities by reflection, so it needs a parameterless constructor. Private,
    // so no caller can create an Order that skips Create() and its invariant checks.
    private Order()
    {
        Currency = DefaultCurrency;

        // Initialized where Currency is known. A field initializer (= Money.Zero(DefaultCurrency))
        // would hard-code the const and ignore a per-order currency, and `Subtotal - Discount` would
        // then throw on every materialized order in a multi-currency future.
        Discount = Money.Zero(Currency);
    }

    // Single-currency system, decided once. If multi-currency ever lands, this const becomes a
    // constructor parameter and AddLine's currency guard starts doing real work.
    public const string DefaultCurrency = "IRR";

    // Cross-aggregate reference BY IDENTITY ONLY (Guid, matching Customer.Id).
    // A `Customer` navigation property here would let application code mutate another aggregate
    // through this one, which breaks the consistency boundary the root exists to protect.
    public Guid CustomerId { get; private set; }

    public string Currency { get; private set; }

    // Assigned once, by AssignNumber, from IDocumentNumberGenerator. Nullable because a Draft
    // legitimately exists before it has a number — but it can never be placed without one.
    public DocumentNumber? Number { get; private set; }

    public OrderStatus Status { get; private set; }

    // private set: computed by the aggregate from its lines, never assigned by a caller (it used to
    // be a public decimal, which lost the currency and invited outside writes).
    public Money Total { get; private set; }

    // Stored, not derived: a discount is a decision somebody made on this document, not a computation.
    public Money Discount { get; private set; }

    // The root keeps the only writable view of its children; outsiders get IReadOnlyCollection, so
    // order.Lines.Add(...) is not even compilable — AddLine/RemoveLine are the only doors.
    private readonly List<OrderLine> _lines = new();

    public IReadOnlyCollection<OrderLine> Lines => _lines;

    // Gross amount before discount: derived from the line snapshots, never stored, never assigned.
    // The previous `Subtotal => Total - Discount` was circular — Total already has the discount off,
    // so it returned subtotal minus TWICE the discount.
    // Read-only computed property: Phase F's EF configuration must Ignore() it (there is no column).
    public Money Subtotal => _lines.Count == 0
        ? Money.Zero(Currency)
        : _lines.Select(l => l.LineTotal).Aggregate((left, right) => left + right);

    public DateTimeOffset? PlacedAt { get; private set; }

    public DateTimeOffset? ShippedAt { get; private set; }

    public DateTimeOffset? DeliveredAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public string? CancellationReason { get; private set; }

    // A Draft order is never valid in a half-built state, so callers don't `new Order()` and assign
    // properties one at a time: the factory enforces the invariant at the moment of creation.
    public static Order Create(Guid customerId)
    {
        Guard.AgainstInvalidState(customerId != Guid.Empty,
            () => new DomainException("A customer is required to create an order."));

        var order = new Order
        {
            CustomerId = customerId,
            Currency = DefaultCurrency,
            Status = OrderStatus.Draft,
            Total = Money.Zero(DefaultCurrency),
            Discount = Money.Zero(DefaultCurrency)
        };

        return order;
    }

    // Ids are assigned by the database, so EF needs this hook; business code never calls it.
    internal Order WithId(Guid id)
    {
        Id = id;
        return this;
    }

    public Order AssignNumber(DocumentNumber number)
    {
        Guard.AgainstNull(number, () => new DomainException("A document number is required."));
        Guard.AgainstInvalidState(Number is null,
            () => new DomainException($"This order already has number {Number}."));
        Guard.AgainstInvalidState(number.Series == DocumentSeries.Order,
            () => new DomainException($"An order number must belong to the Order series, not {number.Series}."));

        Number = number;
        return this;
    }

    public Order AddLine(Guid productId, Money unitPrice, int quantity)
    {
        Guard.AgainstInvalidState(productId != Guid.Empty, () => new DomainException("Product is required."));
        EnsureEditable();
        Guard.AgainstMixedCurrency(new Money(0, Currency), unitPrice);

        // One line per product: re-ordering the same product raises its quantity instead of leaving
        // two lines that a warehouse operator has to mentally add up.
        var existing = _lines.FirstOrDefault(l => l.ProductId == productId);
        if (existing is not null)
            return ChangeLineQuantity(existing.Id, existing.Quantity + quantity);

        var line = OrderLine.Create(Id, productId, unitPrice, quantity);
        _lines.Add(line);
        RecalculateTotal();

        return this;
    }

    public Order ChangeLineQuantity(Guid lineId, int quantity)
    {
        EnsureEditable();

        var line = FindLine(lineId);
        line.ChangeQuantity(quantity);
        RecalculateTotal();

        return this;
    }

    public Order RemoveLine(Guid lineId)
    {
        EnsureEditable();

        var line = FindLine(lineId);
        _lines.Remove(line);          // remove from the backing list: reassigning the collection
        RecalculateTotal();           // would fight EF's change tracker (the old version did exactly that)

        return this;
    }

    // A discount belongs to the DRAFT, never to a finished document. Without EnsureEditable() a
    // cancelled or delivered order could absorb discounts (and emit events) forever.
    //
    // Decision recorded: applying twice REPLACES the discount (latest wins) — it is not additive,
    // because "10 % then 20 %" as two independent facts has no business meaning on one document.
    // Passing Money.Zero is how a discount is removed.
    public Order ApplyDiscount(Money discount, DateTimeOffset now)
    {
        EnsureEditable();

        // Checked against THIS order's currency and against the SUBTOTAL. The old version compared
        // `discount.Amount < Total.Amount`: Total already had the discount removed, so a legitimate
        // 90 discount on a 100 order (after a 40 discount) was refused, and the strict `<` also made a
        // 100 % (free-of-charge) discount impossible.
        Guard.AgainstMixedCurrency(new Money(0, Currency), discount);
        Guard.AgainstInvalidState(discount <= Subtotal,
            () => new DomainException($"Discount {discount} cannot exceed the subtotal {Subtotal}."));

        Discount = discount;
        RecalculateTotal();
        Raise(new OrderDiscountApplied(Id, Number, Discount, Total, now));

        return this;
    }

    public Order PlaceOrder(DateTimeOffset now)
    {
        // Two different rules, two different messages: "empty order" and "wrong status" are
        // different business failures and the API caller needs to know which one to fix.
        Guard.AgainstInvalidState(_lines.Count > 0,
            () => new DomainException("An order needs at least one line before it can be placed."));
        Guard.AgainstNull(Number, () => new DomainException("An order needs a document number before it can be placed."));
        EnsureCanMoveTo(OrderStatus.Placed);

        Status = OrderStatus.Placed;
        PlacedAt = now;
        Raise(new OrderPlaced(Id, Number, Total, _lines.Count, now));

        return this;
    }

    public Order ConfirmOrder(DateTimeOffset now)
    {
        EnsureCanMoveTo(OrderStatus.Confirmed);

        Status = OrderStatus.Confirmed;
        Raise(new OrderConfirmed(Id, Number, Total, now));

        return this;
    }

    public Order ShipOrder(DateTimeOffset now)
    {
        EnsureCanMoveTo(OrderStatus.Shipped);

        Status = OrderStatus.Shipped;
        ShippedAt = now;
        Raise(new OrderShipped(Id, Number, now));

        return this;
    }

    // `now` comes from the caller's IClock instead of DateTime.Now: the domain stays deterministic
    // and the event timestamp is testable.
    public Order DeliverOrder(DateTimeOffset now)
    {
        EnsureCanMoveTo(OrderStatus.Delivered);

        Status = OrderStatus.Delivered;
        DeliveredAt = now;
        Raise(new OrderCompleted(Id, Number, Total, now));

        return this;
    }

    // The old condition (`Shipped || Confirmed || Status != Delivered`) threw on everything except
    // Delivered — i.e. it let you cancel a DELIVERED order and refused to cancel a DRAFT one, the
    // exact opposite of its own error message. The legal set is now stated positively:
    // cancel anything up to (but not including) shipment; Delivered and Cancelled are terminal.
    public Order CancelOrder(DateTimeOffset now, string? reason = null)
    {
        Guard.AgainstInvalidState(Status is OrderStatus.Draft or OrderStatus.Pending
                                       or OrderStatus.Placed or OrderStatus.Confirmed,
            () => new InvalidOrderStateException(Status, OrderStatus.Cancelled));

        var cancelledFrom = Status;
        Status = OrderStatus.Cancelled;
        CancelledAt = now;
        CancellationReason = reason;
        Raise(new OrderCancelled(Id, Number, cancelledFrom, reason, now));

        return this;
    }

    // The whole lifecycle in one readable table. Anything not listed is forbidden, so adding a state
    // later fails loudly (false) rather than accidentally being allowed.
    private static bool CanMove(OrderStatus from, OrderStatus to) => (from, to) switch
    {
        (OrderStatus.Draft, OrderStatus.Placed) => true,
        (OrderStatus.Pending, OrderStatus.Placed) => true,
        (OrderStatus.Placed, OrderStatus.Confirmed) => true,
        (OrderStatus.Confirmed, OrderStatus.Shipped) => true,
        (OrderStatus.Shipped, OrderStatus.Delivered) => true,
        _ => false
    };

    private void EnsureCanMoveTo(OrderStatus target) =>
        Guard.AgainstInvalidState(CanMove(Status, target), () => new InvalidOrderStateException(Status, target));

    // Lines are only mutable while the order is ours to edit; after Placed the customer agreed to it.
    private void EnsureEditable() =>
        Guard.AgainstInvalidState(Status is OrderStatus.Draft or OrderStatus.Pending,
            () => new InvalidOrderStateException(Status, Status));

    private OrderLine FindLine(Guid lineId)
    {
        var line = _lines.FirstOrDefault(l => l.Id == lineId);
        Guard.AgainstNull(line, () => new DomainException($"Line {lineId} does not belong to this order."));
        return line!;
    }

    // The only place that writes Total: subtotal (sum of the LINE price snapshots — so a price change
    // tomorrow cannot rewrite yesterday's order) minus the stored discount. Recomputed from scratch on
    // every change, which is why repeated line edits cannot accumulate drift.
    private void RecalculateTotal() => Total = Subtotal - Discount;
}
