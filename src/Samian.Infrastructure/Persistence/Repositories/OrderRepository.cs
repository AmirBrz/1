using Microsoft.EntityFrameworkCore;
using Samian.Domain.Contracts;
using Samian.Domain.Entities;
using Samian.Domain.ValueObjects;

namespace Samian.Infrastructure.Persistence.Repositories;

// The EF Core ADAPTER for the Domain's IOrderRepository port. The Domain declared the contract (and the
// "complete aggregate, lines included" rule); this class is where that promise is kept. Every read that
// returns an Order calls Include(o => o.Lines) — forgetting it is the single most common repository bug,
// because it produces a valid-looking order with zero lines and the aggregate then refuses to be placed.
public sealed class OrderRepository : IOrderRepository
{
    private readonly SamianDbContext _db;

    public OrderRepository(SamianDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        _db = db;
    }

    public async Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Single-key lookup on a client-assigned Guid: EF's Find would return the TRACKED instance
        // without touching the database (and without the lines), which would silently hand back a
        // half-loaded aggregate. FirstOrDefault on Id always queries, so the shape is predictable.
        return await _db.Orders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
    }

    public async Task<Order?> GetByNumberAsync(DocumentNumber number, CancellationToken cancellationToken = default)
    {
        // Translatable only because OrderConfiguration gave the converted property a ValueComparer:
        // EF compares the PROVIDER strings ("ORD-000123") instead of failing on value-object equality.
        return await _db.Orders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Number == number, cancellationToken);
    }

    public async Task<IReadOnlyList<Order>> GetForCustomerAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        // Materialize first, then sort IN MEMORY: SQLite refuses ORDER BY on DateTimeOffset-typed
        // expressions ("does not support expressions of type 'DateTimeOffset' in ORDER BY clauses"),
        // because it stores them as TEXT with a variable offset that SQL cannot compare chronologically.
        // SQL Server/Postgres would translate this directly; the client-side sort here is bounded (one
        // customer's orders) and provider-specific, which is exactly why the port does not mention it.
        var orders = await _db.Orders
            .Include(o => o.Lines)
            .Where(o => o.CustomerId == customerId)
            .ToListAsync(cancellationToken);

        // Newest first, and a stable tie-break: `PlacedAt` is null for every draft, so without the
        // Id the order of drafts would be whatever the storage engine feels like returning.
        return orders
            .OrderByDescending(o => o.PlacedAt)
            .ThenByDescending(o => o.Id)
            .ToList();
    }

    public async Task<IReadOnlyList<Order>> GetPagedAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        // This is the ONE read allowed to be incomplete: a list screen shows numbers and totals, and
        // loading child lines for 20 orders is 20 times the rows for no reason. The port documents the
        // same nuance, so the adapter is not contradicting the Domain.
        //
        // Skip/Take run in SQL; the DateTimeOffset sort runs after materialization for the same SQLite
        // reason as GetForCustomerAsync — with the accepted caveat that page ORDER across drafts is only
        // deterministic after the in-memory tie-break, which is fine at sample scale but is the first
        // thing to move to a sortable column (e.g. an indexed long timestamp) on a real product.
        var rows = await _db.Orders
            .Skip(page * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return rows
            .OrderByDescending(o => o.PlacedAt)
            .ThenByDescending(o => o.Id)
            .ToList();
    }

    public async Task AddAsync(Order order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);

        // Add cascades to the tracked children (Lines), so one call stages the whole aggregate.
        // No SaveChanges here: the unit of work owns the commit, and a repository that commits turns
        // "place order + deduct stock" into two transactions that can disagree.
        await _db.Orders.AddAsync(order, cancellationToken);
    }
}
