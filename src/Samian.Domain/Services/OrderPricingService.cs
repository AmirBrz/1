using Samian.Domain.Common;
using Samian.Domain.Contracts;
using Samian.Domain.Entities;
using Samian.Domain.ValueObjects;

namespace Samian.Domain.Services;

// A DOMAIN SERVICE whose single job is: ask the policy for a discount and PROVE the answer is safe
// to put on this order. It deliberately does not compute totals — Order owns Total (one source of
// truth), and it deliberately does not mutate the order — the caller applies the result, so the
// use case in Phase E reads: calculate -> check Result -> order.ApplyDiscount(...).
//
// This is where Result earns its keep (unlike the previous version which always returned success):
// there are three genuine business failures below, and a failed pricing decision is a normal outcome
// the API turns into a 400, not a crash.
public sealed class OrderPricingService
{
    private readonly IPricingPolicy _policy;

    // Constructor injection: the service is useless without a rule, and a test can hand it a fake
    // policy and assert the rule was actually consulted.
    public OrderPricingService(IPricingPolicy policy)
    {
        Guard.AgainstNull(policy, () => new ArgumentNullException(nameof(policy)));
        _policy = policy;
    }

    public Result<Money> CalculateDiscount(Order order, DateTimeOffset now, SaleOffer? offer = null)
    {
        Guard.AgainstNull(order, () => new ArgumentNullException(nameof(order)));

        if (order.Lines.Count == 0)
            return Result.Failure<Money>("There is nothing to price: the order has no lines.");

        // Mixed currencies are checked here rather than left to Money's + operator, because the
        // service can name the offending product — a thrown arithmetic error cannot.
        var firstCurrency = order.Lines.First().UnitPrice.Currency;
        var mismatched = order.Lines.FirstOrDefault(l => l.UnitPrice.Currency != firstCurrency);
        if (mismatched is not null)
            return Result.Failure<Money>(
                $"Order mixes '{firstCurrency}' with '{mismatched.UnitPrice.Currency}' " +
                $"on product {mismatched.ProductId}; an order must be priced in one currency.");


        // var context = new PricingContext(order.Subtotal, order.Lines.ToList(), now, order.TenantId);
        // var discount = _policy.DiscountFor(context);


        var context = new PricingContext(order.Subtotal, order.Lines.ToList(), now, order.TenantId, offer);
        var discount = _policy.DiscountFor(context);


        // A policy is a plug-in rule; a buggy rule returning more than the order is worth is caught
        // HERE, at the boundary, instead of corrupting Total. (It cannot return a negative amount:
        // Money makes that value unconstructible, so no check is needed for it.)
        if (discount > order.Subtotal)
            return Result.Failure<Money>(
                $"Discount {discount} exceeds the subtotal {order.Subtotal}.");

        return Result.Success(discount);
    }
}
