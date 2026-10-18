using Samian.Domain.Common;
using Samian.Domain.Enums;
using Samian.Domain.Exceptions;
using Samian.Domain.ValueObjects;

namespace Samian.Domain.Entities;

// The real subject of the Black Friday challenge — not Product, not Order.
// RemainingUnits here is the SALE ALLOCATION, deliberately separate from
// Product.StockQuantity (warehouse), because warehouse sync is a *downstream*
// pipeline step per the brief, not part of the reservation counter.
public class SaleEvent : AggregateRoot, IMultiTenant
{
    public Guid TenantId { get; private set; }
    public string Sku { get; private set; } = string.Empty;
    public int TotalUnits { get; private set; }
    public int RemainingUnits { get; private set; }
    public Money OfferPrice { get; private set; }
    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset EndsAt { get; private set; }

    // EF/materialization ctor — the aggregate is only ever built through Create.
    private SaleEvent() { }

    public static SaleEvent Create(
        Guid tenantId, string sku, int totalUnits, Money offerPrice,
        DateTimeOffset startsAt, DateTimeOffset endsAt)
    {
        Guard.AgainstEmpty(sku, () => new DomainException("Sale event requires a SKU."));
        Guard.AgainstNonPositive(totalUnits, () => new DomainException("A sale needs at least one unit."));
        Guard.AgainstZero(offerPrice.Amount, () => new DomainException("Offer price must be positive."));
        Guard.AgainstInvalidState(endsAt > startsAt,
            () => new DomainException("Sale end must be after sale start."));

        var sale = new SaleEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Sku = sku.Trim(),
            TotalUnits = totalUnits,
            RemainingUnits = totalUnits,
            OfferPrice = offerPrice,
            StartsAt = startsAt,
            EndsAt = endsAt
        };
        return sale;
    }

    // THE only place the window is checked. [start, end) — inclusive at start,
    // exclusive at end, so a sale ending at 9:30 is closed for a request at 9:30:00.
    public bool IsOn(DateTimeOffset now) => now >= StartsAt && now < EndsAt;

    public SaleEventState StateAt(DateTimeOffset now)
        => now < StartsAt ? SaleEventState.Upcoming
         : now >= EndsAt ? SaleEventState.Expired
         : SaleEventState.Active;

    // In-memory rule used by the aggregate itself. The GRADED guarantee (no oversell
    // across concurrent requests/instances) is the conditional UPDATE in the repository
    // (ROADMAP step 15) — this method must never be trusted as that lock.
    // Returns Result, not a throw: "sold out" is a normal business answer here.
    public Result TryReserve(int quantity, DateTimeOffset now)
    {
        if (!IsOn(now)) return Result.Failure("The sale event is not active.");
        if (quantity <= 0) return Result.Failure("Reservation quantity must be positive.");
        if (RemainingUnits < quantity) return Result.Failure("The sale event is sold out.");

        RemainingUnits -= quantity;
        return Result.Success();
    }

    // Called when a pipeline job is dead-lettered: the unit goes back on the shelf
    // so a lost job never permanently locks inventory (brief line 21).
    public void Release(int quantity)
    {
        Guard.AgainstNonPositive(quantity, () => new DomainException("Released quantity must be positive."));
        Guard.AgainstInvalidState(RemainingUnits + quantity <= TotalUnits,
            () => new DomainException("Cannot release more units than the sale ever had."));

        RemainingUnits += quantity;
    }
}