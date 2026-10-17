using Samian.Domain.Common;
using Samian.Domain.Contracts;
using Samian.Domain.Entities;
using Samian.Domain.Exceptions;
using Samian.Domain.ValueObjects;

namespace Samian.Domain.Services;

// Two real IPricingPolicy implementations. An interface with no implementation is decoration: the
// point of the port is that OrderPricingService cannot tell these apart, so swapping one for another
// (or adding a "campaign discount" rule next month) changes a single registration line.
//
// Why they live in Domain/Services: these are BUSINESS rules, not technical concerns. A rule that
// lives in Infrastructure would be an anemic model with extra steps.

// The safe default: used for guests, and the stub to inject in tests that don't care about pricing.
public sealed class NoDiscountPricingPolicy : IPricingPolicy
{
    public Money DiscountFor(Money subtotal, IReadOnlyCollection<OrderLine> lines)
        => Money.Zero(subtotal.Currency);
}

// "X % once the order reaches Y" — the most common ERP pricing rule, and parameterized rather than
// hard-coded so the threshold/rate can come from configuration in Phase F.
public sealed class VolumeDiscountPricingPolicy : IPricingPolicy
{
    private readonly decimal _rate;          // 0.10m = 10 %
    private readonly decimal _thresholdAmount;

    public VolumeDiscountPricingPolicy(decimal rate, decimal thresholdAmount)
    {
        // A policy that accepts rate 1.5 or a negative threshold is a bug that shows up as money
        // leaving the company, so its own construction is guarded.
        Guard.AgainstInvalidState(rate > 0 && rate <= 1,
            () => new DomainException($"Discount rate '{rate}' must be between 0 and 1."));
        Guard.AgainstInvalidState(thresholdAmount > 0,
            () => new DomainException("Discount threshold must be greater than zero."));

        _rate = rate;
        _thresholdAmount = thresholdAmount;
    }

    public Money DiscountFor(Money subtotal, IReadOnlyCollection<OrderLine> lines)
    {
        // The threshold is built in the SUBTOTAL's currency instead of being stored as one currency:
        // the policy stays currency-agnostic and no conversion is invented here.
        if (subtotal < new Money(_thresholdAmount, subtotal.Currency))
            return Money.Zero(subtotal.Currency);

        return subtotal * _rate;
    }
}
