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
    public Money DiscountFor(PricingContext context)
        => Money.Zero(context.Subtotal.Currency);
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

    public Money DiscountFor(PricingContext context)
    {
        // The threshold is built in the SUBTOTAL's currency instead of being stored as one currency:
        // the policy stays currency-agnostic and no conversion is invented here.
        if (context.Subtotal < new Money(_thresholdAmount, context.Subtotal.Currency))
            return Money.Zero(context.Subtotal.Currency);

        return context.Subtotal * _rate;
    }
}

// The Black Friday rule: a rate off the LIST price of the promoted product's lines, but ONLY while
// the window is open — 8:59:59 and 9:30:00 both answer zero. The window lives in the context because
// the policy must stay clockless and data-only (the port's contract).
public sealed class BlackFridayPricingPolicy : IPricingPolicy
{
    public Money DiscountFor(PricingContext context)
    {
        var offer = context.Offer;
        if (offer is null)
            return Money.Zero(context.Subtotal.Currency);

        // Same half-open window as SaleEvent.IsOn: [start, end). One rule, one shape, everywhere.
        if (context.Now < offer.StartsAt || context.Now >= offer.EndsAt)
            return Money.Zero(context.Subtotal.Currency);

        var promoted = context.Lines
            .Where(l => l.ProductId == offer.ProductId)
            .Select(l => l.LineTotal)
            .DefaultIfEmpty(Money.Zero(context.Subtotal.Currency))
            .Aggregate((left, right) => left + right);

        return promoted * offer.DiscountRate;
    }
}

// Precedence rule for the whole pricing area: policies do NOT stack — the composite asks every
// rule and keeps the LARGEST single discount. Why not "sum them": two overlapping campaigns
// silently combining into a 45 % off is the kind of number nobody in marketing approved; "largest
// wins" is one line to test and one sentence to defend in the README (assumption #4).
// Every policy must answer in the SUBTOTAL's currency (they all build Money.Zero from it) — that
// is what makes the > comparison here safe.
public sealed class PricingPolicyComposite : IPricingPolicy
{
    private readonly IReadOnlyList<IPricingPolicy> _policies;

    public PricingPolicyComposite(params IPricingPolicy[] policies)
    {
        Guard.AgainstInvalidState(policies is { Length: > 0 },
            () => new DomainException("A pricing composite needs at least one policy."));
        _policies = policies;
    }

    public Money DiscountFor(PricingContext context)
    {
        Money? best = null;
        foreach (var policy in _policies)
        {
            var candidate = policy.DiscountFor(context);
            if (best is null || candidate > best)
                best = candidate;
        }
        return best ?? Money.Zero(context.Subtotal.Currency);
    }
}
