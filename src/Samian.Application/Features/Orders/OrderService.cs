using Samian.Application.Common;
using Samian.Application.Contracts;
using Samian.Application.Validation;
using Samian.Domain.Common;
using Samian.Domain.Contracts;
using Samian.Domain.Entities;
using Samian.Domain.Enums;
using Samian.Domain.Services;
using Samian.Domain.ValueObjects;

namespace Samian.Application.Features.Orders;

public sealed record CreateOrderRequest(Guid CustomerId, string Currency = Order.DefaultCurrency);

public sealed record AddOrderLineRequest(Guid ProductId, int Quantity);

public sealed record ChangeOrderLineQuantityRequest(int Quantity);

public sealed record ApplyDiscountRequest(decimal Amount);

public sealed record CancelOrderRequest(string? Reason);

public sealed record OrderLineResponse(
    Guid Id,
    Guid ProductId,
    decimal UnitPriceAmount,
    string Currency,
    int Quantity,
    decimal LineTotal);

public sealed record OrderResponse(
    Guid Id,
    string? Number,
    string Status,
    string Currency,
    decimal Subtotal,
    decimal Discount,
    decimal Total,
    IReadOnlyList<OrderLineResponse> Lines,
    DateTimeOffset? PlacedAt,
    DateTimeOffset? ShippedAt,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? CancelledAt,
    string? CancellationReason);

// The port a controller sees. Notice what is NOT on it: no Order, no OrderStatus, no Money, no
// CancellationToken-free overloads. The aggregate cannot be reached through the API, which is the
// practical difference between "layered" code and onion code.
public interface IOrderService
{
    Task<OrderResponse> CreateDraftAsync(CreateOrderRequest request, CancellationToken cancellationToken = default);

    Task<OrderResponse> AddLineAsync(Guid orderId, AddOrderLineRequest request, CancellationToken cancellationToken = default);

    Task<OrderResponse> ChangeLineQuantityAsync(Guid orderId, Guid lineId, ChangeOrderLineQuantityRequest request, CancellationToken cancellationToken = default);

    Task<OrderResponse> RemoveLineAsync(Guid orderId, Guid lineId, CancellationToken cancellationToken = default);

    Task<OrderResponse> ApplyDiscountAsync(Guid orderId, ApplyDiscountRequest request, CancellationToken cancellationToken = default);

    Task<OrderResponse> ApplyAutomaticDiscountAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<OrderResponse> PlaceAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<OrderResponse> ConfirmAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<OrderResponse> ShipAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<OrderResponse> DeliverAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<OrderResponse> CancelAsync(Guid orderId, CancelOrderRequest request, CancellationToken cancellationToken = default);

    Task<OrderResponse> GetAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrderResponse>> GetForCustomerAsync(Guid customerId, CancellationToken cancellationToken = default);
}

public sealed class CreateOrderRequestValidator : IValidator<CreateOrderRequest>
{
    public IReadOnlyCollection<string> Validate(CreateOrderRequest target)
    {
        var messages = new ValidationMessages();

        messages.AddIf(target.CustomerId == Guid.Empty, "A customer id is required.");
        messages.AddIfNot(RegexRules.CurrencyCode().IsMatch(target.Currency),
            "Currency must be three uppercase letters, for example IRR or USD.");

        return messages.ToList();
    }
}

public sealed class AddOrderLineRequestValidator : IValidator<AddOrderLineRequest>
{
    public IReadOnlyCollection<string> Validate(AddOrderLineRequest target)
    {
        var messages = new ValidationMessages();

        messages.AddIf(target.ProductId == Guid.Empty, "A product id is required.");
        messages.AddIf(target.Quantity is < 1 or > 10_000,
            "Quantity must be between 1 and 10000. (The upper bound is a typo guard, not a business rule.)");

        return messages.ToList();
    }
}

public sealed class ChangeOrderLineQuantityRequestValidator : IValidator<ChangeOrderLineQuantityRequest>
{
    public IReadOnlyCollection<string> Validate(ChangeOrderLineQuantityRequest target)
    {
        var messages = new ValidationMessages();

        messages.AddIf(target.Quantity is < 1 or > 10_000, "Quantity must be between 1 and 10000.");

        return messages.ToList();
    }
}

public sealed class ApplyDiscountRequestValidator : IValidator<ApplyDiscountRequest>
{
    public IReadOnlyCollection<string> Validate(ApplyDiscountRequest target)
    {
        var messages = new ValidationMessages();

        // Negative is refused here; "bigger than the subtotal" is refused by the aggregate. The
        // request validator knows nothing about the order, and must not pretend to.
        messages.AddIf(target.Amount < 0, "Discount cannot be negative — pass 0 to remove a discount.");

        return messages.ToList();
    }
}

public sealed class OrderService : IOrderService
{
    private readonly IOrderRepository _orders;
    private readonly IProductRepository _products;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IDocumentNumberGenerator _numberGenerator;
    private readonly StockAvailabilityService _stock;
    private readonly OrderPricingService _pricing;
    private readonly ICacheService _cache;
    private readonly IValidator<CreateOrderRequest> _createValidator;
    private readonly IValidator<AddOrderLineRequest> _addLineValidator;
    private readonly IValidator<ChangeOrderLineQuantityRequest> _changeQuantityValidator;
    private readonly IValidator<ApplyDiscountRequest> _discountValidator;

    // Eleven collaborators is a lot, and it is the honest number for a use-case orchestrator. When a
    // service starts needing this much, the fix in a real system is to split by use case (PlaceOrder,
    // ShipOrder as separate handlers) — the dependencies do not go away, they just stop being shared.
    public OrderService(
        IOrderRepository orders,
        IProductRepository products,
        IUnitOfWork unitOfWork,
        IClock clock,
        IDocumentNumberGenerator numberGenerator,
        StockAvailabilityService stock,
        OrderPricingService pricing,
        ICacheService cache,
        IValidator<CreateOrderRequest> createValidator,
        IValidator<AddOrderLineRequest> addLineValidator,
        IValidator<ChangeOrderLineQuantityRequest> changeQuantityValidator,
        IValidator<ApplyDiscountRequest> discountValidator)
    {
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(products);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(numberGenerator);
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(pricing);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(createValidator);
        ArgumentNullException.ThrowIfNull(addLineValidator);
        ArgumentNullException.ThrowIfNull(changeQuantityValidator);
        ArgumentNullException.ThrowIfNull(discountValidator);

        _orders = orders;
        _products = products;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _numberGenerator = numberGenerator;
        _stock = stock;
        _pricing = pricing;
        _cache = cache;
        _createValidator = createValidator;
        _addLineValidator = addLineValidator;
        _changeQuantityValidator = changeQuantityValidator;
        _discountValidator = discountValidator;
    }

    public async Task<OrderResponse> CreateDraftAsync(CreateOrderRequest request, CancellationToken cancellationToken = default)
    {
        _createValidator.EnsureValid(request);

        var order = Order.Create(request.CustomerId, request.Currency);

        // The number is taken at creation, not at placement: in an ERP a draft is already a document
        // somebody can quote, and a gap-free counter must not be advanced by a draft that is thrown away.
        // (If the business ever prefers "number only when placed", this block moves to PlaceAsync —
        // one line, because nothing else assumes a draft is numbered.)
        var number = await _numberGenerator.NextAsync(DocumentSeries.Order, cancellationToken);
        order.AssignNumber(number);

        await _orders.AddAsync(order, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ToResponse(order);
    }

    public async Task<OrderResponse> AddLineAsync(Guid orderId, AddOrderLineRequest request, CancellationToken cancellationToken = default)
    {
        _addLineValidator.EnsureValid(request);

        var order = await RequireOrderAsync(orderId, cancellationToken);

        // The product is loaded to read its CURRENT price, which is then frozen onto the line. This is
        // the only place the two aggregates touch, and it is a read of one + a write of the other —
        // never a navigation property from one into the other.
        var product = await _products.GetByIdAsync(request.ProductId, cancellationToken)
                      ?? throw new ResourceNotFoundException("Product", request.ProductId);

        // Order.Currency vs Product.Price.Currency mismatch surfaces here as a DomainException from
        // Money's own guard — which becomes a 400 with a sentence the caller can act on.
        order.AddLine(product.Id, product.Price, request.Quantity);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await _cache.RemoveAsync(CacheKeys.Order(order.Id), cancellationToken);

        return ToResponse(order);
    }

    public async Task<OrderResponse> ChangeLineQuantityAsync(Guid orderId, Guid lineId, ChangeOrderLineQuantityRequest request, CancellationToken cancellationToken = default)
    {
        _changeQuantityValidator.EnsureValid(request);

        var order = await RequireOrderAsync(orderId, cancellationToken);

        order.ChangeLineQuantity(lineId, request.Quantity);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await _cache.RemoveAsync(CacheKeys.Order(order.Id), cancellationToken);

        return ToResponse(order);
    }

    public async Task<OrderResponse> RemoveLineAsync(Guid orderId, Guid lineId, CancellationToken cancellationToken = default)
    {
        var order = await RequireOrderAsync(orderId, cancellationToken);

        order.RemoveLine(lineId);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await _cache.RemoveAsync(CacheKeys.Order(order.Id), cancellationToken);

        return ToResponse(order);
    }

    public async Task<OrderResponse> ApplyDiscountAsync(Guid orderId, ApplyDiscountRequest request, CancellationToken cancellationToken = default)
    {
        _discountValidator.EnsureValid(request);

        var order = await RequireOrderAsync(orderId, cancellationToken);

        // The currency comes from the ORDER, never from the request: a client that asks to discount
        // "500 USD" on an IRR order is a bug or an attack, and there is nothing to negotiate.
        order.ApplyDiscount(new Money(request.Amount, order.Currency), _clock.UtcNow);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await _cache.RemoveAsync(CacheKeys.Order(order.Id), cancellationToken);

        return ToResponse(order);
    }

    public async Task<OrderResponse> ApplyAutomaticDiscountAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var order = await RequireOrderAsync(orderId, cancellationToken);

        // STRATEGY in action: the rule (volume discount? none?) was chosen once at composition root.
        // This method does not know which policy it is asking, and the policy does not know an Order
        // exists — it receives subtotal + lines and returns a suggestion.
        var suggestion = _pricing.CalculateDiscount(order);

        if (suggestion.IsFailure)
            throw new AppValidationException(suggestion.Error!);

        var discount = suggestion.ValueOrThrow();

        // The service suggested, the AGGREGATE decides: ApplyDiscount re-checks editability and the
        // subtotal ceiling. Trusting the policy's answer here would let a bad policy write a bad total.
        order.ApplyDiscount(discount, _clock.UtcNow);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await _cache.RemoveAsync(CacheKeys.Order(order.Id), cancellationToken);

        return ToResponse(order);
    }

    public async Task<OrderResponse> PlaceAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var order = await RequireOrderAsync(orderId, cancellationToken);

        // Availability is CHECKED (not deducted) at placement: the customer commits, warehouse stock is
        // still untouched. Deduction happens at shipment — the documented ERP trade-off, and the reason
        // two orders can both be placed against the last unit and one of them fails at ship.
        var products = await LoadProductsForAsync(order, cancellationToken);

        var availability = _stock.VerifyAvailability(order, products);
        if (availability.IsFailure)
            throw new BusinessConflictException(availability.Error!);

        if (order.Number is null)
        {
            var number = await _numberGenerator.NextAsync(DocumentSeries.Order, cancellationToken);
            order.AssignNumber(number);
        }

        order.PlaceOrder(_clock.UtcNow);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await _cache.RemoveAsync(CacheKeys.Order(order.Id), cancellationToken);

        return ToResponse(order);
    }

    public async Task<OrderResponse> ConfirmAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var order = await RequireOrderAsync(orderId, cancellationToken);

        order.ConfirmOrder(_clock.UtcNow);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await _cache.RemoveAsync(CacheKeys.Order(order.Id), cancellationToken);

        return ToResponse(order);
    }

    public async Task<OrderResponse> ShipAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var order = await RequireOrderAsync(orderId, cancellationToken);

        var products = await LoadProductsForAsync(order, cancellationToken);

        // Verify-then-act inside the domain service, so no product is decremented unless ALL lines can
        // be satisfied. Then ONE SaveChanges for both aggregates: two aggregates, one transaction.
        //
        // Committing stock first and shipping second (two commits) would leave the warehouse minus one
        // unit and the order still "Confirmed" if ShipOrder refused the transition. The ordering rule
        // "deduct before ship" is about the DOMAIN calls, not about the number of transactions.
        var deduction = _stock.DeductStock(order, products);
        if (deduction.IsFailure)
            throw new BusinessConflictException(deduction.Error!);

        order.ShipOrder(_clock.UtcNow);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // Stock moved, so the cached product read models are now wrong. The order's own key is
        // invalidated by OrderCacheInvalidationHandler (a domain event), which is the pattern for
        // cross-cutting consequences of a write.
        foreach (var product in products)
            await _cache.RemoveAsync(CacheKeys.Product(product.Id), cancellationToken);

        return ToResponse(order);
    }

    public async Task<OrderResponse> DeliverAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        var order = await RequireOrderAsync(orderId, cancellationToken);

        order.DeliverOrder(_clock.UtcNow);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await _cache.RemoveAsync(CacheKeys.Order(order.Id), cancellationToken);

        return ToResponse(order);
    }

    public async Task<OrderResponse> CancelAsync(Guid orderId, CancelOrderRequest request, CancellationToken cancellationToken = default)
    {
        var order = await RequireOrderAsync(orderId, cancellationToken);

        // Restoring stock on cancellation is deliberately NOT done here: it is the same cross-aggregate
        // rule as shipment, so it belongs in StockAvailabilityService (an IncreaseStock path) once the
        // business decides whether a cancelled shipment should restock automatically. Leaving it out is
        // better than inventing a rule the domain has not agreed to.
        order.CancelOrder(_clock.UtcNow, request.Reason);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await _cache.RemoveAsync(CacheKeys.Order(order.Id), cancellationToken);

        return ToResponse(order);
    }

    public Task<OrderResponse> GetAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        return _cache.GetOrSetAsync(
            CacheKeys.Order(orderId),
            async () =>
            {
                var order = await _orders.GetByIdAsync(orderId, cancellationToken);
                if (order is null)
                    throw new ResourceNotFoundException("Order", orderId);

                return ToResponse(order);
            },
            CacheDurations.OrderReadModels,
            cancellationToken);
    }

    public async Task<IReadOnlyList<OrderResponse>> GetForCustomerAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        // Awaited rather than returned: Task<T> is invariant, so the cached List<OrderResponse> has to
        // be unwrapped before it can be handed out as an IReadOnlyList.
        return await _cache.GetOrSetAsync<List<OrderResponse>>(
            CacheKeys.CustomerOrders(customerId),
            async () =>
            {
                var orders = await _orders.GetForCustomerAsync(customerId, cancellationToken);
                return orders.Select(ToResponse).ToList();
            },
            CacheDurations.OrderReadModels,
            cancellationToken);
    }

    private async Task<Order> RequireOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await _orders.GetByIdAsync(orderId, cancellationToken);

        // "not found" is a 404, and it is decided here rather than letting the aggregate throw: an
        // aggregate cannot be missing, so a missing id is not a domain violation.
        return order ?? throw new ResourceNotFoundException("Order", orderId);
    }

    private async Task<IReadOnlyCollection<Product>> LoadProductsForAsync(Order order, CancellationToken cancellationToken)
    {
        // One batch query for the whole order. Looping GetByIdAsync per line is the N+1 the repository
        // port's GetByIdsAsync exists to prevent.
        var ids = order.Lines.Select(l => l.ProductId).Distinct().ToList();

        return ids.Count == 0
            ? Array.Empty<Product>()
            : await _products.GetByIdsAsync(ids, cancellationToken);
    }

    private static OrderResponse ToResponse(Order order) =>
        new(
            order.Id,
            order.Number?.Value,
            order.Status.ToString(),
            order.Currency,
            order.Subtotal.Amount,
            order.Discount.Amount,
            order.Total.Amount,
            order.Lines.Select(ToLineResponse).ToList(),
            order.PlacedAt,
            order.ShippedAt,
            order.DeliveredAt,
            order.CancelledAt,
            order.CancellationReason);

    private static OrderLineResponse ToLineResponse(OrderLine line) =>
        new(line.Id, line.ProductId, line.UnitPrice.Amount, line.UnitPrice.Currency, line.Quantity, line.LineTotal.Amount);
}
