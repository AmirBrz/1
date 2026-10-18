using Samian.Application.Common;
using Samian.Application.Contracts;
using Samian.Application.Validation;
using Samian.Domain.Contracts;
using Samian.Domain.Entities;
using Samian.Domain.ValueObjects;

namespace Samian.Application.Features.Products;

public sealed record CreateProductRequest(string Name, decimal PriceAmount, string Currency, int StockQuantity = 0, string? Sku = null);

public sealed record ChangePriceRequest(decimal Amount, string Currency);

public sealed record RestockRequest(int Quantity);

// A READ MODEL, not the aggregate: flattened Money into amount + currency, no behavior, safe to
// serialize to Redis and to version independently of the domain. If a controller returned `Product`,
// every private setter and every aggregate method would become part of the public API contract.
public sealed record ProductResponse(
    Guid Id,
    string Name,
    string Sku,
    decimal PriceAmount,
    string Currency,
    int StockQuantity);

public interface IProductService
{
    Task<ProductResponse> CreateAsync(CreateProductRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProductResponse>> ListAsync(int page, int pageSize, CancellationToken cancellationToken = default);

    Task<ProductResponse> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ProductResponse> ChangePriceAsync(Guid id, ChangePriceRequest request, CancellationToken cancellationToken = default);

    Task<ProductResponse> RestockAsync(Guid id, RestockRequest request, CancellationToken cancellationToken = default);
}

public sealed class CreateProductRequestValidator : IValidator<CreateProductRequest>
{
    public IReadOnlyCollection<string> Validate(CreateProductRequest target)
    {
        var messages = new ValidationMessages();

        messages.AddIfNot(RegexRules.ProductName().IsMatch(target.Name),
            "Product name must be 2-100 characters, start with a letter, and contain no symbols outside . & / ( ) - and spaces.");
        messages.AddIfNot(RegexRules.CurrencyCode().IsMatch(target.Currency),
            "Currency must be three uppercase letters, for example IRR or USD.");
        messages.AddIf(target.PriceAmount < 0, "Price cannot be negative.");
        messages.AddIf(target.StockQuantity < 0, "Opening stock cannot be negative.");
        messages.AddIf(target.Sku is not null && (target.Sku.Length == 0 || target.Sku.Length > 40),
            "SKU, when supplied, must be 1-40 characters.");

        return messages.ToList();
    }
}

public sealed class ChangePriceRequestValidator : IValidator<ChangePriceRequest>
{
    public IReadOnlyCollection<string> Validate(ChangePriceRequest target)
    {
        var messages = new ValidationMessages();

        messages.AddIfNot(RegexRules.CurrencyCode().IsMatch(target.Currency),
            "Currency must be three uppercase letters, for example IRR or USD.");
        messages.AddIf(target.Amount <= 0, "A price must be greater than zero — use stock or a discount instead of a free price.");

        return messages.ToList();
    }
}

public sealed class RestockRequestValidator : IValidator<RestockRequest>
{
    public IReadOnlyCollection<string> Validate(RestockRequest target)
    {
        var messages = new ValidationMessages();

        // The domain also refuses this (Product.IncreaseStock guards it), and that is not redundant:
        // the guard protects the invariant, this one protects the user from a 400 whose message is a
        // stack-trace-shaped surprise. Both say no; this one says it in field terms.
        messages.AddIf(target.Quantity <= 0, "Restock quantity must be greater than zero.");

        return messages.ToList();
    }
}

public sealed class ProductService : IProductService
{
    private readonly IProductRepository _products;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICacheService _cache;
    private readonly ITenantProvider _tenant;
    private readonly IValidator<CreateProductRequest> _createValidator;
    private readonly IValidator<ChangePriceRequest> _changePriceValidator;
    private readonly IValidator<RestockRequest> _restockValidator;

    public ProductService(
        IProductRepository products,
        IUnitOfWork unitOfWork,
        ICacheService cache,
        IValidator<CreateProductRequest> createValidator,
        IValidator<ChangePriceRequest> changePriceValidator,
        IValidator<RestockRequest> restockValidator,
        ITenantProvider tenant)
    {
        ArgumentNullException.ThrowIfNull(products);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(createValidator);
        ArgumentNullException.ThrowIfNull(changePriceValidator);
        ArgumentNullException.ThrowIfNull(restockValidator);

        _products = products;
        _unitOfWork = unitOfWork;
        _cache = cache;
        _tenant = tenant;
        _createValidator = createValidator;
        _changePriceValidator = changePriceValidator;
        _restockValidator = restockValidator;
    }

    public async Task<ProductResponse> CreateAsync(CreateProductRequest request, CancellationToken cancellationToken = default)
    {
        _createValidator.EnsureValid(request);

        // The catalogue belongs to the signed-in tenant: without this the product would be persisted
        // with TenantId = Guid.Empty and the global filter would hide it from every real-tenant read.
        if (!_tenant.HasTenant || _tenant.TenantId == Guid.Empty)
            throw new AppValidationException("A tenant context is required to create a product (sign in to a tenant, or send X-Tenant-Id).");

        // The application service never assigns Name/Price/Stock itself: Product.Create enforces its own
        // invariant, so a half-built product cannot reach the database even if a caller skips validation.
        var product = Product.Create(request.Name, new Money(request.PriceAmount, request.Currency), request.StockQuantity, _tenant.TenantId, request.Sku);

        await _products.AddAsync(product, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // A new row is not in any cached list, so every page key has to go. (A production system would
        // version the list key instead of deleting N keys; with a small page count this is the honest fix.)
        await InvalidateAsync(product.Id, cancellationToken);

        return ToResponse(product);
    }

    public async Task<IReadOnlyList<ProductResponse>> ListAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        // CACHE-ASIDE (the pattern the challenge asks Redis for): the cache holds the read model, the
        // database stays the source of truth, and the aggregate never knows caching exists.
        //
        // The cached type is List<ProductResponse> rather than IReadOnlyList<...> because the adapter
        // round-trips JSON: a concrete class deserializes, an interface does not. The await is what
        // converts them — Task<T> is invariant, so Task<List<T>> is not a Task<IReadOnlyList<T>>.
        return await _cache.GetOrSetAsync<List<ProductResponse>>(
            CacheKeys.ProductList(_tenant.TenantId, page, pageSize),
            async () =>
            {
                var products = await _products.GetPagedAsync(page, pageSize, cancellationToken);
                return products.Select(ToResponse).ToList();
            },
            CacheDurations.ProductReadModels,
            cancellationToken);
    }

    public Task<ProductResponse> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _cache.GetOrSetAsync<ProductResponse>(
            CacheKeys.Product(_tenant.TenantId, id),
            async () =>
            {
                var product = await _products.GetByIdAsync(id, cancellationToken);
                if (product is null)
                    throw new ResourceNotFoundException("Product", id);

                return ToResponse(product);
            },
            CacheDurations.ProductReadModels,
            cancellationToken);
    }

    public async Task<ProductResponse> ChangePriceAsync(Guid id, ChangePriceRequest request, CancellationToken cancellationToken = default)
    {
        _changePriceValidator.EnsureValid(request);

        var product = await _products.GetByIdAsync(id, cancellationToken)
                      ?? throw new ResourceNotFoundException("Product", id);

        // An ERP product carries one currency; re-pricing it in another one is a different product.
        // This rule lives here rather than in Money because it is about the SAME aggregate over time,
        // which the value object cannot see.
        if (!string.Equals(request.Currency, product.Price.Currency, StringComparison.Ordinal))
            throw new AppValidationException(
                $"Product '{product.Name}' is priced in {product.Price.Currency}; it cannot be re-priced in {request.Currency}.");

        product.ChangePrice(new Money(request.Amount, request.Currency));

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await InvalidateAsync(product.Id, cancellationToken);

        return ToResponse(product);
    }

    public async Task<ProductResponse> RestockAsync(Guid id, RestockRequest request, CancellationToken cancellationToken = default)
    {
        _restockValidator.EnsureValid(request);

        var product = await _products.GetByIdAsync(id, cancellationToken)
                      ?? throw new ResourceNotFoundException("Product", id);

        product.IncreaseStock(request.Quantity);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await InvalidateAsync(product.Id, cancellationToken);

        return ToResponse(product);
    }

    private async Task InvalidateAsync(Guid productId, CancellationToken cancellationToken)
    {
        await _cache.RemoveAsync(CacheKeys.Product(_tenant.TenantId, productId), cancellationToken);

        // Best-effort but explicit: page 0..9 is enough for a sample ERP catalogue. Leaving the list
        // keys alive is the classic "price changed but the shop still shows the old price" bug.
        for (var page = 0; page < 10; page++)
            await _cache.RemoveAsync(CacheKeys.ProductList(_tenant.TenantId, page, 20), cancellationToken);
    }

    private static ProductResponse ToResponse(Product product) =>
        new(product.Id, product.Name, product.Sku, product.Price.Amount, product.Price.Currency, product.StockQuantity);
}
