using Samian.Domain.Common;
using Samian.Domain.Exceptions;
using Samian.Domain.ValueObjects;

namespace Samian.Domain.Entities;

// Product is its OWN aggregate root (it has its own identity, its own stock invariant, and its own
// repository), not a member of the Order aggregate. OrderLine therefore keeps ProductId + a price
// snapshot instead of a live Product reference: two aggregates are only ever joined by identity.
public class Product : AggregateRoot, IMultiTenant
{
    private Product() { }

    public Guid TenantId { get; private set; }

    // The stock-keeping unit. A Black Friday sale event is declared FOR a SKU, so the warehouse row
    // the out-of-band pipeline decrements must be findable by that same key — name is display text
    // ("65 inch 4K TV"), the SKU is the contract between the sale, the reservation and the stock.
    public string Sku { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    // Money, not decimal: a price without a currency is not a price.
    public Money Price { get; private set; }

    // Renamed from `Quantity` (which is also the name on OrderLine for something completely different).
    public int StockQuantity { get; private set; }

    // Was `private static`: nothing outside the class could ever create a Product — not the API,
    // not a test, not the seed data. A factory that cannot be called is not a factory.
    // we will replace `Guid.Empty` defaults with the real ITenantProvider value at each call site.
    // it's empty and optional for seed and success compilation.
    public static Product Create(string name, Money price, int stockQuantity = 0, Guid tenantId = default, string? sku = null)
    {
        Guard.AgainstEmpty(name, () => new DomainException("Product name is required."));
        Guard.AgainstNegative(stockQuantity, () => new DomainException("Stock cannot be negative."));

        // A SKU always exists: supplied verbatim when the caller knows the warehouse code, otherwise
        // derived from the name so no product can be born unmappable to a sale.
        var normalizedSku = string.IsNullOrWhiteSpace(sku) ? Slug(name) : sku.Trim();
        Guard.AgainstEmpty(normalizedSku, () => new DomainException("Product SKU is required."));

        return new Product
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Sku = normalizedSku,
            Name = name,
            Price = price,
            StockQuantity = stockQuantity
        };
    }

    // Uppercase, spaces to hyphens: "65 inch 4K TV" -> "65-INCH-4K-TV". A readable default, not a
    // claim to be a real warehouse coding scheme (that would be a domain service or an external feed).
    private static string Slug(string value) => value.Trim().ToUpperInvariant().Replace(' ', '-');

    public Product Rename(string name)
    {
        Guard.AgainstEmpty(name, () => new DomainException("Product name is required."));
        Name = name;
        return this;
    }

    // Price changes are an explicit business act (and the reason order lines must snapshot prices).
    public Product ChangePrice(Money newPrice)
    {
        Price = newPrice;
        return this;
    }

    public Product IncreaseStock(int quantity)
    {
        // The old version accepted negative quantities, so "increase" could silently decrease stock
        // and bypass the insufficiency check on the other method.
        Guard.AgainstNonPositive(quantity, () => new DomainException("Stock increase must be greater than zero."));
        StockQuantity += quantity;
        return this;
    }

    public Product DecreaseStock(int quantity)
    {
        Guard.AgainstNonPositive(quantity, () => new DomainException("Stock decrease must be greater than zero."));
        Guard.AgainstNegative(StockQuantity - quantity, () => new InsufficientStockException(Name));
        StockQuantity -= quantity;
        return this;
    }

    public bool HasStockFor(int quantity) => quantity > 0 && StockQuantity >= quantity;
}
