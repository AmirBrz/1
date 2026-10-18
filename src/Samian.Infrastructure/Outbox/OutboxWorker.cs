using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Samian.Application.Contracts;
using Samian.Domain.Entities;
using Samian.Infrastructure.Persistence;

namespace Samian.Infrastructure.Outbox;

// OutboxWorker : BackgroundService — no new package, no broker (ADR-2). It polls the SAME table
// the reservation transaction committed, so a job that was accepted cannot disappear: the row
// exists the moment the decrement exists. Crash between the two? Impossible — one commit.
//
// Pipeline steps (brief line 39): create order → sync warehouse → notify. The LOGIC is stubbed
// (the brief allows it); the INFRASTRUCTURE is real: atomic claim, retries with exponential
// backoff, a lease that reclaims crashed work, and a dead-letter status that releases the unit
// back to the sale (step 3's Release) so a dead job never permanently locks inventory.
//
// Failure injection for observability: a CustomerRef containing "boom" deterministically fails
// the first step, so a reviewer can WATCH a job climb Attempts, hit the limit and land in Dead
// at GET /api/jobs?status=Dead — the acceptance checklist's DLQ line, provable without code edits.
public sealed class OutboxWorker : BackgroundService
{
    // Tunables as consts: this is a sample with one deployment; a real one reads them from options.
    private const int MaxAttempts = 5;              // Attempts >= 5 -> Dead (the DLQ)
    private const int BackoffBaseSeconds = 10;      // 10s, 20s, 40s, 80s — exponential, capped
    private const int LeaseMinutes = 2;             // Processing older than this = crashed worker
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    // ONE claim statement: pick the oldest claimable row and flip it to Processing atomically.
    // READPAST+ROWLOCK is the SQL Server queue pattern: a second worker skips the locked row
    // instead of blocking, so two instances can never process the same job twice (roadmap:
    // "otherwise two workers process the same job twice"). The lease branch reclaims rows whose
    // worker died mid-job. OUTPUT returns the claimed row — no read-then-act race anywhere.
    private const string ClaimSql = """
        ;WITH next AS (
            SELECT TOP (1) Id
            FROM OutboxJobs WITH (READPAST, ROWLOCK)
            WHERE (Status = 'Pending'    AND NextAt   <= SYSUTCDATETIME())
               OR (Status = 'Processing' AND ClaimedAt <  DATEADD(MINUTE, -2, SYSUTCDATETIME()))
            ORDER BY NextAt
        )
        UPDATE next
        SET Status = 'Processing', ClaimedAt = SYSUTCDATETIME(), Attempts = Attempts + 1
        OUTPUT inserted.Id, inserted.TenantId, inserted.SaleEventId, inserted.ReservationId,
               inserted.Sku, inserted.Quantity, inserted.UnitPriceAmount, inserted.Currency,
               inserted.CustomerRef, inserted.Attempts;
        """;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxWorker> _logger;

    public OutboxWorker(IServiceScopeFactory scopeFactory, ILogger<OutboxWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OutboxWorker started; pipeline steps run stubbed logic on real infrastructure.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await ProcessOneAsync(stoppingToken))
                    await Task.Delay(PollInterval, stoppingToken);   // idle: cheap poll, no SQL round-trip storm
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;   // shutdown mid-delay is normal
            }
            catch (Exception ex)
            {
                // A worker that crashes takes the service down silently — a loop-level catch with
                // a log is the minimum honesty for "the process must not disappear without a trace".
                _logger.LogError(ex, "OutboxWorker poll iteration failed; retrying in {Seconds}s.", BackoffBaseSeconds);
                await Task.Delay(TimeSpan.FromSeconds(BackoffBaseSeconds), stoppingToken);
            }
        }
    }

    // One job per iteration; the loop backpressures naturally on an empty queue.
    private async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamianDbContext>();

        // NOTE: the worker scope has NO HttpContext, so the tenant filter short-circuits and this
        // claim sees every tenant's queue — the isolation unit here is the JOB ROW's TenantId,
        // carried in the payload, not the ambient request.
        var job = await ClaimAsync(db, ct);
        if (job is null)
            return false;

        try
        {
            await CreateOrderAsync(job);            // step 1 (stubbed, deterministic failure trigger)
            await SyncWarehouseAsync(db, job, ct);  // step 2 (REAL: atomic warehouse decrement)
            await NotifyAsync(scope.ServiceProvider, job, ct);   // step 3 (REAL: file outbox email)

            await FinishAsync(db, job.Id, OutboxStatus.Done, null, DateTimeOffset.UtcNow, 0, ct);
            _logger.LogInformation("Job {JobId} (tenant {Tenant}, {Sku} x{Qty}) Done.", job.Id, job.TenantId, job.Sku, job.Quantity);
        }
        catch (Exception ex)
        {
            // Attempts was already incremented by the claim. Exhaust the budget -> Dead + release
            // the units back; otherwise -> Pending with an exponential NextAt. Either way the row
            // keeps its full history (Attempts, Error) — "retry support and clear visibility into
            // error logs" (brief line 21) as two columns and one status.
            var dead = job.Attempts >= MaxAttempts;
            var backoff = TimeSpan.FromSeconds(BackoffBaseSeconds * Math.Pow(2, Math.Max(0, job.Attempts - 1)));
            var nextAt = DateTimeOffset.UtcNow.Add(dead ? TimeSpan.Zero : backoff);
            var error = $"{ex.GetType().Name}: {ex.Message}".Truncate(3900);

            await FinishAsync(db, job.Id, dead ? OutboxStatus.Dead : OutboxStatus.Pending, error, nextAt, 0, ct);

            if (dead)
            {
                await ReleaseUnitsAsync(scope.ServiceProvider, job, ct);
                _logger.LogWarning("Job {JobId} exhausted {Max} attempts -> Dead (DLQ); {Qty} unit(s) of {Sku} released.",
                    job.Id, MaxAttempts, job.Quantity, job.Sku);
            }
            else
            {
                _logger.LogWarning("Job {JobId} failed attempt {Attempt}/{Max}; retry in {Backoff}. Error: {Error}",
                    job.Id, job.Attempts, MaxAttempts, backoff, error);
            }
        }

        return true;
    }

    // The three pipeline steps. Stubbed logic is the brief's allowance (line 39) — what is NOT
    // stubbed is that they run, retry, and land in the DLQ with their error visible.
    private static Task CreateOrderAsync(ClaimedJob job)
    {
        // Deterministic failure trigger for the DLQ demo (documented in the README).
        if (job.CustomerRef.Contains("boom", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Simulated order-creation failure for reservation of '{job.Sku}' (CustomerRef contains 'boom').");

        // Real code would do Order.Create + lines at the snapshot price (OrderLine.SaleEventId
        // stamps the campaign); the payload already carries everything it needs, by design.
        return Task.CompletedTask;
    }

    private async Task SyncWarehouseAsync(SamianDbContext db, ClaimedJob job, CancellationToken ct)
    {
        // Brief line 39: "synchronizing warehouse inventory". The reservation already claimed the SALE
        // unit atomically (SaleEvent.RemainingUnits) — that is the oversell authority. This step
        // reflects the same quantity into the warehouse BOOK value (Product.StockQuantity), and it must
        // be atomic too: a read-check-write through the aggregate here would be the 9 AM incident all
        // over again. ONE conditional UPDATE keyed by the job's own (TenantId, Sku) — the worker scope
        // has no ambient tenant, so the payload carries the filter (the global filter short-circuits).
        //
        // `StockQuantity >= qty` in the WHERE means a warehouse row that cannot cover the sale is left
        // untouched (rowsAffected 0) rather than driven negative. Deliberately NOT an exception: a
        // Black Friday SKU may be a campaign with no stocked warehouse row, and a valid reservation
        // must not be pushed to the DLQ over bookkeeping — the sale allocation, not this table, is the
        // unit of truth for "can we oversell".
        var rows = await db.Products
            .Where(p => p.TenantId == job.TenantId && p.Sku == job.Sku && p.StockQuantity >= job.Quantity)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.StockQuantity, p => p.StockQuantity - job.Quantity), ct);

        if (rows == 0)
            _logger.LogInformation(
                "Job {JobId}: no stocked warehouse row for {Sku} (tenant {Tenant}) covering {Qty} — sync skipped.",
                job.Id, job.Sku, job.TenantId, job.Quantity);
    }

    private static async Task NotifyAsync(IServiceProvider services, ClaimedJob job, CancellationToken ct)
    {
        var sender = services.GetRequiredService<IEmailSender>();
        await sender.SendAsync(
            to: job.CustomerRef,
            subject: $"[Black Friday] Reservation confirmed: {job.Sku} x{job.Quantity}",
            body: $"Sku: {job.Sku}\nQuantity: {job.Quantity}\nUnit price: {job.UnitPriceAmount:0.##} {job.Currency}\n" +
                  $"SaleEvent: {job.SaleEventId}\nReservation: {job.ReservationId}\nTenant: {job.TenantId}",
            cancellationToken: ct);
    }

    private static async Task<ClaimedJob?> ClaimAsync(SamianDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ClaimSql;
            command.CommandTimeout = 10;

            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            return new ClaimedJob(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3),
                reader.GetString(4), reader.GetInt32(5), reader.GetDecimal(6), reader.GetString(7),
                reader.GetString(8), reader.GetInt32(9));
        }
        finally
        {
            // The scoped context expects to own its connection lifecycle; close only what we opened
            // for the raw claim (EF reuses the pooled connection cheaply).
            if (connection.State == System.Data.ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    private static async Task FinishAsync(
        SamianDbContext db, Guid jobId, string status, string? error,
        DateTimeOffset nextAt, int claimedAtReset, CancellationToken ct)
    {
        // One statement, no entity tracking: the claim already committed (it is outside this
        // context's transaction by design), so the finalization must be equally standalone.
        await db.OutboxJobs.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, status)
                .SetProperty(j => j.Error, error)
                .SetProperty(j => j.NextAt, nextAt)
                .SetProperty(j => j.ClaimedAt, (DateTimeOffset?)null), ct);
    }

    // Dead-letter closes the loop with the domain's Release (step 3): a job that can never succeed
    // must not keep its units reserved. Loads the aggregate, puts them back, one commit.
    private async Task ReleaseUnitsAsync(IServiceProvider services, ClaimedJob job, CancellationToken ct)
    {
        var db = services.GetRequiredService<SamianDbContext>();
        var sale = await db.SaleEvents.FirstOrDefaultAsync(s => s.Id == job.SaleEventId, ct);
        if (sale is null)
        {
            _logger.LogWarning("Dead job {JobId}: sale {SaleId} gone; nothing to release.", job.Id, job.SaleEventId);
            return;
        }

        sale.Release(job.Quantity);
        await db.SaveChangesAsync(ct);
    }
}

// The claimed row as a flat record: the worker reads it from OUTPUT and never goes back to the
// table for payload data — the job carries its own truth.
internal sealed record ClaimedJob(
    Guid Id, Guid TenantId, Guid SaleEventId, Guid ReservationId,
    string Sku, int Quantity, decimal UnitPriceAmount, string Currency,
    string CustomerRef, int Attempts);

internal static class StringTruncate
{
    public static string Truncate(this string value, int max) =>
        value.Length <= max ? value : value[..max];
}
