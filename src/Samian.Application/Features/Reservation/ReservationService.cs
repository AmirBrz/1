using Samian.Application.Common;
using Samian.Application.Contracts;
using Samian.Application.Validation;
using Samian.Domain.Contracts;
using Samian.Domain.Entities;

namespace Samian.Application.Features.Reservations;

public sealed record ReserveRequest(Guid SaleEventId, int Quantity, string CustomerRef);

// Read model. UnitPrice is the OFFER price as decided at reservation time — the number the
// downstream pipeline must use when it finally creates the order line snapshot (Step 11).
public sealed record ReservationResponse(
    Guid Id,
    Guid TenantId,
    Guid SaleEventId,
    string Sku,
    int Quantity,
    decimal UnitPriceAmount,
    string UnitPriceCurrency,
    DateTimeOffset ReservedAt);

public interface IReservationService
{
    Task<ReservationResponse> ReserveAsync(ReserveRequest request, CancellationToken cancellationToken = default);
}

public sealed class ReserveRequestValidator : IValidator<ReserveRequest>
{
    public IReadOnlyCollection<string> Validate(ReserveRequest target)
    {
        var messages = new ValidationMessages();

        messages.AddIf(target.SaleEventId == Guid.Empty, "A sale event id is required.");
        messages.AddIf(target.Quantity is < 1 or > 1000,
            "Reservation quantity must be between 1 and 1000. (Typo guard; one burst request must not eat the allocation.)");
        messages.AddIf(string.IsNullOrWhiteSpace(target.CustomerRef), "A customer reference is required for the reservation.");

        return messages.ToList();
    }
}

// The use case the whole challenge is about: answer the customer INSTANTLY (ROADMAP: brief line 20),
// hand fulfilment to the pipeline later. Six ordered steps, and the order is the design:
public sealed class ReservationService : IReservationService
{
    private readonly ISaleEventRepository _sales;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly ITenantProvider _tenant;
    private readonly ICacheService _cache;
    private readonly IReservationJobQueue _jobs;
    private readonly IValidator<ReserveRequest> _reserveValidator;

    public ReservationService(
        ISaleEventRepository sales,
        IUnitOfWork unitOfWork,
        IClock clock,
        ITenantProvider tenant,
        ICacheService cache,
        IReservationJobQueue jobs,
        IValidator<ReserveRequest> reserveValidator)
    {
        ArgumentNullException.ThrowIfNull(sales);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(reserveValidator);

        _sales = sales;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _tenant = tenant;
        _cache = cache;
        _jobs = jobs;
        _reserveValidator = reserveValidator;
    }

    public async Task<ReservationResponse> ReserveAsync(ReserveRequest request, CancellationToken cancellationToken = default)
    {
        _reserveValidator.EnsureValid(request);

        var now = _clock.UtcNow;
        var tenantId = _tenant.HasTenant && _tenant.TenantId != Guid.Empty
            ? _tenant.TenantId
            : throw new AppValidationException("A tenant context is required to reserve (X-Tenant-Id).");

        // 1. Load the sale for the window/invariant checks. Read-only use: the loaded aggregate's
        //    RemainingUnits may be a millisecond stale by design — step 3 does NOT trust it.
        var sale = await _sales.GetAsync(request.SaleEventId, cancellationToken);
        if (sale is null || sale.TenantId != tenantId)
            throw new ResourceNotFoundException("Sale event", request.SaleEventId);

        // 2. Outside the window → 409. IsOn is the ONLY place the window is read (domain, Step 3):
        //    the service asks the aggregate instead of comparing timestamps itself.
        if (!sale.IsOn(now))
            throw new BusinessConflictException(
                $"Sale '{sale.Sku}' is {sale.StateAt(now)} — reservations are accepted only while it is Active.");

        // One transaction spans the atomic decrement AND the reservation row: without it the UPDATE
        // auto-commits on its own and a failure before SaveChanges would sell a unit with no record.
        await _unitOfWork.BeginTransactionAsync(cancellationToken);

        var reserved = await _sales.TryReserveAsync(sale.Id, tenantId, request.Quantity, cancellationToken);
        if (!reserved)
        {
            // Nothing was decremented, but the transaction must still be closed cleanly.
            await _unitOfWork.CommitTransactionAsync(cancellationToken);   // empty commit, cheaper than a rollback API on the port
            throw new BusinessConflictException($"Sale '{sale.Sku}' is sold out — no units left to reserve.");
        }

        var reservation = Reservation.Record(tenantId, sale.Id, sale.Sku, request.Quantity, request.CustomerRef, now);
        await _sales.AddReservationAsync(reservation, cancellationToken);

        // THE outbox row (step 17): staged on the SAME context INSIDE the open transaction, so
        // decrement + reservation + job commit together or not at all. "Job accepted, then lost"
        // — the exact incident failure — becomes unconstructible: there is no committed decrement
        // without its job row, and no job row without the units being sold.
        await _jobs.StageAsync(new ReservationJob(
            tenantId, sale.Id, reservation.Id, sale.Sku, request.Quantity,
            sale.OfferPrice.Amount, sale.OfferPrice.Currency, request.CustomerRef), cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);   // joins the open transaction

        await _unitOfWork.CommitTransactionAsync(cancellationToken);

        // The counter just moved, so the display value is wrong by one reservation. Delete the
        // volatile key AFTER the commit (a delete before a rolled-back transaction would have
        // repopulated the cache from the old value); the metadata key survives unchanged.
        await _cache.RemoveAsync(CacheKeys.SaleStatus(tenantId, sale.Id), cancellationToken);

        // 6. Answer immediately — order creation, warehouse sync and notification run out of band
        //    in the OutboxWorker; GET /api/jobs makes the pipeline observable.
        return new ReservationResponse(
            reservation.Id, tenantId, sale.Id, sale.Sku, request.Quantity,
            sale.OfferPrice.Amount, sale.OfferPrice.Currency, now);
    }
}