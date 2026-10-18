using Samian.Domain.Entities;

namespace Samian.Domain.Contracts;

// Four reads/writes, nothing else. No IQueryable crosses the port (AGENTS.md rule),
// lookups return nullable for "not found".
public interface ISaleEventRepository
{
    Task<SaleEvent?> GetAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<SaleEvent>> ListActiveAsync(
        Guid tenantId, DateTimeOffset now, CancellationToken ct = default);

    Task AddAsync(SaleEvent saleEvent, CancellationToken ct = default);

    Task AddReservationAsync(Reservation reservation, CancellationToken ct = default);

    // THE method: check-and-decrement in ONE atomic database operation.
    // false = not enough units. The decision is made by the database engine,
    // not by an in-memory read, which is what makes it safe across concurrent
    // requests AND across multiple app instances.
    Task<bool> TryReserveAsync(
        Guid saleEventId, Guid tenantId, int quantity, CancellationToken ct = default);
}