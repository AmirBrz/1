using Samian.Domain.Entities;
using Samian.Domain.ValueObjects;

namespace Samian.Domain.Contracts;

// The RULE of how much a customer is discounted. The port exists so the rule can change without
// touching the aggregate or the service that applies it.
//
// Design decisions worth defending:
// - SYNCHRONOUS: pricing is pure in-memory math. If a rule ever needs data from outside (customer
//   tier, campaign table), the DATA is passed in as a parameter — the calculation is not made async
//   to hide an I/O call that belongs to the caller.
// - It receives DATA (subtotal + lines), NOT the Order. Handing the aggregate to a collaborator
//   would let that collaborator call AddLine/ApplyDiscount/CancelOrder on it: the consistency
//   boundary would exist in name only.
// - It returns a discount AMOUNT, never a total. Totals belong to the aggregate (one source of truth).
// - No Result here: a policy cannot really "fail", it either finds a rule applicable (discount 0 is
//   a valid answer) or not. Failure handling lives in OrderPricingService, which validates the answer.
public interface IPricingPolicy
{
    Money DiscountFor(Money subtotal, IReadOnlyCollection<OrderLine> lines);
}
