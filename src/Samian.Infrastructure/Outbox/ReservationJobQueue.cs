using Microsoft.EntityFrameworkCore;
using Samian.Application.Contracts;

namespace Samian.Infrastructure.Outbox;

// The adapter half of IReservationJobQueue. The whole implementation is "add a row to the SAME
// scoped DbContext the reservation is using" — deliberately NO SaveChanges here: the unit of
// work's open transaction commits the decrement, the reservation row and this job row together
// or not at all. That "together" is the brief's line-21 guarantee rebuilt as one commit, and
// it is why an in-memory Channel<T> queue was rejected (ADR-2): it can disagree with the database.
public sealed class ReservationJobQueue : IReservationJobQueue
{
    private readonly Persistence.SamianDbContext _db;

    public ReservationJobQueue(Persistence.SamianDbContext db) => _db = db;

    public async Task StageAsync(ReservationJob job, CancellationToken cancellationToken = default)
    {
        await _db.OutboxJobs.AddAsync(new OutboxJobRow
        {
            Id = Guid.NewGuid(),                 // client-assigned, same doctrine as every Guid key
            TenantId = job.TenantId,
            SaleEventId = job.SaleEventId,
            ReservationId = job.ReservationId,
            Sku = job.Sku,
            Quantity = job.Quantity,
            UnitPriceAmount = job.UnitPriceAmount,
            Currency = job.Currency,
            CustomerRef = job.CustomerRef,
            Status = OutboxStatus.Pending,
            Attempts = 0,
            NextAt = DateTimeOffset.UtcNow      // claimable immediately
        }, cancellationToken);
    }
}

// GET /api/jobs reads the pipeline through Application, never through a controller query:
// one Application interface per controller is the AGENTS.md rule the existing controllers follow.
public sealed class PipelineInspector : IPipelineInspector
{
    private readonly Persistence.SamianDbContext _db;
    private readonly ITenantProvider _tenant;

    public PipelineInspector(Persistence.SamianDbContext db, ITenantProvider tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public async Task<IReadOnlyList<JobResponse>> GetJobsAsync(
        string? statusFilter, CancellationToken cancellationToken = default)
    {
        // OutboxJobRow is NOT IMultiTenant (storage machinery, invisible to the global filter),
        // so this query carries the tenant explicitly — the isolation rule reaches the pipeline
        // view the same way it reaches every table: one tenant sees its own jobs and errors only.
        var query = _db.OutboxJobs.AsNoTracking().Where(j => j.TenantId == _tenant.TenantId);

        if (!string.IsNullOrWhiteSpace(statusFilter))
            query = query.Where(j => j.Status == statusFilter);

        var rows = await query
            .OrderByDescending(j => j.NextAt)
            .Take(200)   // an observability endpoint paginates lazily; 200 is the honest review view
            .ToListAsync(cancellationToken);

        return rows.Select(j => new JobResponse(
            j.Id, j.Status, j.Attempts, j.NextAt, j.Error, j.SaleEventId, j.Sku, j.Quantity)).ToList();
    }
}
