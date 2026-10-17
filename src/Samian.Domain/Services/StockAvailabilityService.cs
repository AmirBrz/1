using Samian.Domain.Common;
using Samian.Domain.Entities;

namespace Samian.Domain.Services;

// A DOMAIN SERVICE: the rule spans two aggregates (Order and Product), so it belongs to neither
// entity. This is the answer to "who decrements Product.Stock?" — not Order (that would reach into
// another aggregate), not the application service (that would put a business rule in the orchestration
// layer), and definitely not inside Product knowing about Order.
//
// Per project convention it RETURNS Result instead of throwing: stock availability is checked before
// anything is written, and "not enough stock" is a business outcome the caller reports as a 409/400.
public sealed class StockAvailabilityService
{
    // Pure check: nothing is mutated, so it is safe to call repeatedly and easy to unit-test.
    public Result VerifyAvailability(Order order, IReadOnlyCollection<Product> products)
    {
        foreach (var line in order.Lines)
        {
            var product = products.FirstOrDefault(p => p.Id == line.ProductId);

            if (product is null)
                return Result.Failure($"Product {line.ProductId} is not available in the catalogue.");

            if (!product.HasStockFor(line.Quantity))
                return Result.Failure($"Insufficient stock for '{product.Name}': " +
                                      $"{line.Quantity} requested, {product.StockQuantity} in stock.");
        }

        return Result.Success();
    }

    // Verify-then-act: because the first pass proves every line is satisfiable, the second pass cannot
    // fail halfway through and leave some products decremented and some not.
    // (True atomicity is the transaction's job — this only removes the *domain* failure mode.)
    public Result DeductStock(Order order, IReadOnlyCollection<Product> products)
    {
        var verification = VerifyAvailability(order, products);
        if (verification.IsFailure)
            return verification;

        foreach (var line in order.Lines)
        {
            var product = products.First(p => p.Id == line.ProductId);
            product.DecreaseStock(line.Quantity);
        }

        return Result.Success();
    }
}
