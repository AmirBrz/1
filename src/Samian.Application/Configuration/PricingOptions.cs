namespace Samian.Application.Configuration;

// Which pricing rule runs is a DEPLOYMENT decision (marketing owns the threshold, not the codebase),
// so the strategy is named here and constructed once in the composition root. The alternative —
// `if (customer.IsVip) new VolumeDiscount(...)` inside a service — is the same switch statement with
// a bigger blast radius.
public sealed class PricingOptions
{
    public const string SectionName = "Pricing";

    public PricingStrategy Strategy { get; set; } = PricingStrategy.Volume;

    // 0.05 = 5 %. Kept as a fraction rather than "5" so a typo cannot turn a 5 % discount into a 500 %
    // one; the policy guards the 0..1 range as well.
    public decimal DiscountRate { get; set; } = 0.05m;

    // In the order's currency. With IRR and a 5 % rate, a 1,000,000 IRR order earns 50,000 IRR off.
    public decimal ThresholdAmount { get; set; } = 1_000_000m;
}

public enum PricingStrategy
{
    None,
    Volume
}
