using Samian.Domain.Contracts;
using Samian.Domain.Entities;
using Samian.Domain.Exceptions;
using Samian.Domain.Services;
using Samian.Domain.ValueObjects;

namespace Samian.Tests;

// ROADMAP Step 22, test #3: "8:59:59 no promo → 9:00:00 promo → 9:30:00 no promo; plus 'promotion
// wins over volume, they do not stack'; plus the existing discount > subtotal guard stays."
//
// Pure domain, no infrastructure — the policies are SYNCHRONOUS and DATA-ONLY by design (port
// contract: never touches a repository or a clock), so testing them is just calling a method.
public sealed class PricingAndClockTests
{
    private static readonly Guid ProductId = Guid.NewGuid();
    private static readonly Guid SaleEventId = Guid.NewGuid();
    private static readonly DateTimeOffset WindowStart = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2026, 11, 27, 9, 30, 0, TimeSpan.Zero);

    private static SaleOffer MakeOffer(decimal rate = 0.35m) =>
        new(SaleEventId, ProductId, rate, WindowStart, WindowEnd);

    // A line of 2 × 1000 IRR = 2000 subtotal; the offered product IS this line's product.
    private static PricingContext MakeContext(DateTimeOffset now, SaleOffer? offer = null)
    {
        var line = OrderLine.Create(Guid.NewGuid(), ProductId, new Money(1000, "IRR"), 2);
        return new PricingContext(
            new Money(2000, "IRR"),
            new[] { line },
            now,
            TenantId: Guid.NewGuid(),
            Offer: offer);
    }

    [Fact]
    public void BlackFriday_8_59_59_before_window_answers_zero()
    {
        var policy = new BlackFridayPricingPolicy();
        var justBefore = WindowStart.AddSeconds(-1);  // 08:59:59

        var discount = policy.DiscountFor(MakeContext(justBefore, MakeOffer()));

        Assert.Equal(Money.Zero("IRR"), discount);
    }

    [Fact]
    public void BlackFriday_9_00_00_start_of_window_applies_discount()
    {
        var policy = new BlackFridayPricingPolicy();

        var discount = policy.DiscountFor(MakeContext(WindowStart, MakeOffer()));

        Assert.Equal(new Money(700, "IRR"), discount);  // 2000 × 0.35
    }

    [Fact]
    public void BlackFriday_9_30_00_end_of_window_is_exclusive_and_answers_zero()
    {
        var policy = new BlackFridayPricingPolicy();

        var discount = policy.DiscountFor(MakeContext(WindowEnd, MakeOffer()));

        Assert.Equal(Money.Zero("IRR"), discount);
    }

    [Fact]
    public void Composite_picks_largest_single_discount_does_not_stack()
    {
        // Volume would give 200 (2000 × 10%), BlackFriday gives 700 (2000 × 35%).
        // Non-stacking = max(200, 700) = 700, not 900. ROADMAP assumption #4 proven in one line.
        var volume = new VolumeDiscountPricingPolicy(0.10m, 1000m);
        var blackFriday = new BlackFridayPricingPolicy();
        var composite = new PricingPolicyComposite(volume, blackFriday);

        var discount = composite.DiscountFor(MakeContext(WindowStart, MakeOffer()));

        Assert.Equal(new Money(700, "IRR"), discount);
    }

    [Fact]
    public void Composite_outside_window_falls_back_to_volume()
    {
        var volume = new VolumeDiscountPricingPolicy(0.10m, 1000m);
        var blackFriday = new BlackFridayPricingPolicy();
        var composite = new PricingPolicyComposite(volume, blackFriday);

        var discount = composite.DiscountFor(MakeContext(WindowStart.AddSeconds(-1), MakeOffer()));

        Assert.Equal(new Money(200, "IRR"), discount);  // 2000 × 10% volume only
    }

    [Fact]
    public void Composite_requires_at_least_one_policy()
    {
        Assert.Throws<DomainException>(() => new PricingPolicyComposite());
    }

    [Fact]
    public void Order_discount_exceeding_subtotal_throws_domain_exception()
    {
        // The guard from AGENTS.md: "discount may equal subtotal (100%) but never exceed it."
        var order = Order.Create(Guid.NewGuid());
        order.AddLine(Guid.NewGuid(), new Money(500, "IRR"), 2);  // Subtotal 1000

        Assert.Throws<DomainException>(() =>
            order.ApplyDiscount(new Money(1001, "IRR"), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Order_discount_equal_to_subtotal_is_allowed()
    {
        var order = Order.Create(Guid.NewGuid());
        order.AddLine(Guid.NewGuid(), new Money(500, "IRR"), 2);  // Subtotal 1000

        order.ApplyDiscount(new Money(1000, "IRR"), DateTimeOffset.UtcNow);

        Assert.Equal(Money.Zero("IRR"), order.Total);
    }
}
