using Samian.Domain.Common;
using Samian.Domain.Exceptions;
using Samian.Domain.ValueObjects;

namespace Samian.Domain.Entities;

// OrderLine is a CHILD entity inside the Order aggregate: it has its own identity (so you can point
// at "line 3"), but it is only ever loaded/saved through Order, and only Order calls its methods.
public class OrderLine : Entity<Guid>
{
    private OrderLine() { }

    // Set by the root, never by a caller: a child cannot re-parent itself to another order.
    public Guid OrderId { get; private set; }

    // Reference to the Product aggregate BY IDENTITY ONLY (was `int`, which could never match
    // Product.Id of type Guid, and was paired with a public `Product` navigation that let callers
    // reach into another aggregate).
    public Guid ProductId { get; private set; }

    // THE PRICE SNAPSHOT. This is the whole point of the child entity: `ol.Product.Price` in the old
    // CalculateTotalPrice read tomorrow's price into yesterday's order (and NRE'd whenever Product
    // wasn't loaded). The amount the customer agreed to is frozen on the line.
    public Money UnitPrice { get; private set; }

    public int Quantity { get; private set; }

    // Derived, never stored or assigned: quantity × the snapshot price.
    public Money LineTotal => UnitPrice * Quantity;

    // WHY the promotion is NOT Order.Discount: Discount is per-order and applying it REPLACES the
    // previous one — a campaign applied that way would discount items that are not in the sale and
    // lose the price of record an invoice must show. An offer price is a PRICE, so it goes into the
    // UnitPrice snapshot; this id is the proof of WHICH sale justified it.
    public Guid? SaleEventId { get; private set; }
    public static OrderLine Create(Guid orderId, Guid productId, Money unitPrice, int quantity, Guid? saleEventId = null)
    {
        // Business counts are never zero: quantity 0 is not "an order line of nothing", it is a bug.
        Guard.AgainstNonPositive(quantity, () => new DomainException("Order line quantity must be at least 1."));

        return new OrderLine
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            ProductId = productId,
            UnitPrice = unitPrice,
            Quantity = quantity,
            SaleEventId = saleEventId
        };
    }

    // Mutation goes through the root (Order.ChangeLineQuantity), which recalculates the total after
    // this returns — a public setter would let the total and the lines disagree.
    internal void ChangeQuantity(int quantity)
    {
        Guard.AgainstNonPositive(quantity, () => new DomainException("Order line quantity must be at least 1."));
        Quantity = quantity;
    }

    // EF assigns the key after the first SaveChanges; for an in-memory aggregate tree the child
    // already carries a Guid from Create, so this hook exists only for the database-materialized path.
    internal void AssignId(Guid id) => Id = id;
}
