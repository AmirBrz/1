using Samian.Application.Common;
using Samian.Application.Contracts;
using Samian.Application.Validation;
using Samian.Domain.Contracts;
using Samian.Domain.Entities;
using Samian.Domain.Enums;
using Samian.Domain.ValueObjects;

namespace Samian.Application.Features.Sales;

public sealed record CreateSaleRequest(
    string Sku,
    int TotalUnits,
    decimal OfferAmount,
    string Currency,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt);

// A read model: Money flattened, state as string, computed at THIS instant — never the aggregate.
public sealed record SaleStatusResponse(
    Guid Id,
    Guid TenantId,
    string Sku,
    int TotalUnits,
    int RemainingUnits,
    decimal OfferAmount,
    string OfferCurrency,
    string State,          // Upcoming | Active | Expired — computed, never stored
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt);

// Reserve lives in this slice's CONTRACT today, but the atomic flow (TryReserveAsync + outbox row
// in one transaction) is Step 10 — it is listed on the interface so the controller in Step 18 has
// exactly one door to bind to.
public interface ISaleService
{
    Task<SaleStatusResponse> CreateAsync(CreateSaleRequest request, CancellationToken cancellationToken = default);

    Task<SaleStatusResponse> GetStatusAsync(Guid saleEventId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SaleStatusResponse>> ListActiveAsync(CancellationToken cancellationToken = default);
}

public sealed class CreateSaleRequestValidator : IValidator<CreateSaleRequest>
{
    public IReadOnlyCollection<string> Validate(CreateSaleRequest target)
    {
        var messages = new ValidationMessages();

        messages.AddIfNot(RegexRules.Sku().IsMatch(target.Sku),
            "SKU must be 1-40 characters of letters, digits or '-' (for example 65INCH-TV).");
        messages.AddIf(target.TotalUnits is < 1 or > 1000,
            "Sale quantity must be between 1 and 1000. (The bounds are typo guards; the brief recommends 50-200 for load tests.)");
        messages.AddIfNot(RegexRules.CurrencyCode().IsMatch(target.Currency),
            "Currency must be three uppercase letters, for example IRR or USD.");
        messages.AddIf(target.OfferAmount <= 0, "Offer price must be greater than zero.");
        // The window rule the domain also enforces; saying it here turns a 400-with-stack-message into
        // a per-field message the caller can fix before it ever reaches an aggregate.
        messages.AddIf(target.EndsAt <= target.StartsAt, "Sale end must be after sale start.");

        return messages.ToList();
    }
}

public sealed class SaleService : ISaleService
{
    private readonly ISaleEventRepository _sales;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly ITenantProvider _tenant;
    private readonly ICacheService _cache;
    private readonly IValidator<CreateSaleRequest> _createValidator;

    public SaleService(
        ISaleEventRepository sales,
        IUnitOfWork unitOfWork,
        IClock clock,
        ITenantProvider tenant,
        ICacheService cache,
        IValidator<CreateSaleRequest> createValidator)
    {
        ArgumentNullException.ThrowIfNull(sales);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(createValidator);

        _sales = sales;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _tenant = tenant;
        _createValidator = createValidator;
        _cache = cache;
    }

    public async Task<SaleStatusResponse> CreateAsync(CreateSaleRequest request, CancellationToken cancellationToken = default)
    {
        _createValidator.EnsureValid(request);

        var tenantId = RequireTenant();

        // "One active sale per SKU per tenant" is a CROSS-sale rule the aggregate cannot see (it would
        // need another SaleEvent), so it is checked here as a normal business conflict → 409.
        // Deliberately a friendly guard, not a lock: the real concurrency story is units, not creation.
        var now = _clock.UtcNow;
        var conflicts = await _sales.ListActiveAsync(tenantId, now, cancellationToken);
        if (conflicts.Any(s => string.Equals(s.Sku, request.Sku.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new BusinessConflictException($"Tenant already has an active sale for SKU '{request.Sku}'.");

        var sale = SaleEvent.Create(
            tenantId, request.Sku, request.TotalUnits,
            new Money(request.OfferAmount, request.Currency), request.StartsAt, request.EndsAt);

        await _sales.AddAsync(sale, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return ToResponse(sale, now);
    }

    // ctor: add ICacheService cache (field _cache, ThrowIfNull as the others)

    public Task<SaleStatusResponse> GetStatusAsync(Guid saleEventId, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();

        // CACHE-ASIDE, 5 s, DISPLAY-ONLY: a shopper hammering the status page during the burst never
        // reaches SQL Server, and a reservation NEVER reads this key — so the worst a stale cache can
        // cause is a badge that says "almost gone" for five seconds too long. That sentence is the
        // consistency trade-off the brief demands (line 46) and belongs in the README verbatim.
        return _cache.GetOrSetAsync(
            CacheKeys.SaleStatus(tenantId, saleEventId),
            async () =>
            {
                var sale = await _sales.GetAsync(saleEventId, cancellationToken);

                // Wrong tenant answers NOT FOUND, not FORBIDDEN (unchanged reasoning).
                if (sale is null || sale.TenantId != tenantId)
                    throw new ResourceNotFoundException("Sale event", saleEventId);

                return ToResponse(sale, _clock.UtcNow);
            },
            CacheDurations.SaleStatus,
            cancellationToken);
    }

    public async Task<IReadOnlyList<SaleStatusResponse>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();

        var now = _clock.UtcNow;
        var sales = await _sales.ListActiveAsync(tenantId, now, cancellationToken);

        return sales.Select(s => ToResponse(s, now)).ToList();
    }

    private Guid RequireTenant()
    {
        // Fail-fast even before the middleware (Step 19) is in place: a missing tenant must not run
        // a query at all, because a forgotten filter returning another tenant's rows is the worst
        // failure mode this platform has.
        if (!_tenant.HasTenant || _tenant.TenantId == Guid.Empty)
            throw new AppValidationException("A tenant context is required for every sale operation (X-Tenant-Id).");

        return _tenant.TenantId;
    }

    private static SaleStatusResponse ToResponse(SaleEvent sale, DateTimeOffset now) =>
        new(sale.Id, sale.TenantId, sale.Sku, sale.TotalUnits, sale.RemainingUnits,
            sale.OfferPrice.Amount, sale.OfferPrice.Currency, sale.StateAt(now).ToString(),
            sale.StartsAt, sale.EndsAt);
}