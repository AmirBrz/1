using Samian.Domain.Entities;
using Samian.Domain.ValueObjects;

namespace Samian.Domain.Contracts;

// The RULE of how much a customer is discounted. The port exists so the rule can change without
// touching the aggregate or the service that applies it.
//
// Design decisions worth defending:
// - SYNCHRONOUS and PURE: pricing is in-memory math. If a rule ever needs data from outside
//   (customer tier, campaign table), the DATA is passed in as a parameter — the calculation is
//   not made async to hide an I/O call that belongs to the caller. PricingContext IS that moment:
//   the use case loads the extra data, the policy never touches a repository.
// - It receives DATA, NOT the Order. Handing the aggregate to a collaborator would let that
//   collaborator call AddLine/ApplyDiscount/CancelOrder on it.
// - It returns a discount AMOUNT, never a total. Totals belong to the aggregate (one source of truth).
public interface IPricingPolicy
{

    Money DiscountFor(PricingContext context);

    //Money DiscountFor(Money subtotal, IReadOnlyCollection<OrderLine> lines);
}

// Everything a pure pricing rule may legitimately know. `Now` is in the context because a
// time-window rule (Black Friday) must be able to ask "is the sale on?" without owning a clock —
// the same IClock-discipline the aggregates already follow. `TenantId` is here so a rule can scope
// itself to one tenant's campaign without the policy becoming a data-access object.

// The context grows exactly one optional field when the first time-window rule arrives —
// the DATA comes in, the policy still never touches a repository or a clock.
public sealed record PricingContext(
    Money Subtotal,
    IReadOnlyCollection<OrderLine> Lines,
    DateTimeOffset Now,
    Guid TenantId,
    SaleOffer? Offer = null);          // null = no campaign active; callers pass it, policies read it

// A resolved, already-priced offer: the use case turns a SaleEvent (+ the list price it discounts)
// into a RATE, so the policy stays pure math. ProductId because lines reference products, not SKUs —
// resolving SKU→product is the use case's lookup (it arrives with the Step 15 repository).
public sealed record SaleOffer(
    Guid SaleEventId,
    Guid ProductId,
    decimal DiscountRate,              // 0.35 = 35 % off; SaleEvent's OfferPrice vs list price resolves here
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt);
