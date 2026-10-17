using Samian.Domain.Entities;
using Samian.Domain.ValueObjects;

namespace Samian.Domain.Contracts;

// A PORT: the interface is owned by the Domain, the EF Core implementation is an adapter in
// Infrastructure. This is a "collection of Order aggregates" abstraction, not a table.
//
// Specific interfaces, no generic IRepository<T>: a generic one invites GetAll().Where(...) and puts
// SQL back into the domain. No IQueryable returned anywhere for the same reason.
public interface IOrderRepository
{
    // `Order?` — "not found" is a normal outcome, and a non-nullable Task<Order> forces the
    // implementation to either throw or lie.
    //
    // CONTRACT FOR EVERY ORDER-RETURNING READ BELOW: return the COMPLETE aggregate, OrderLines included
    // (the EF adapter must .Include(o => o.Lines)). An implementation that forgets it returns an order
    // that looks empty, and PlaceOrder then throws "an order needs at least one line" for an order that
    // has twelve lines in the database — a bug that appears only at runtime, far from its cause.
    Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Order?> GetByNumberAsync(DocumentNumber number, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Order>> GetForCustomerAsync(Guid customerId, CancellationToken cancellationToken = default);

    // Paging parameters belong on the port: `GetPagedAsync()` with no arguments cannot page anything.
    // This is the one read that MAY project to a summary (a list screen does not need child lines) —
    // a richer PagedRequest/PagedResult contract belongs to Application, once the queries exist.
    Task<IReadOnlyList<Order>> GetPagedAsync(int page, int pageSize, CancellationToken cancellationToken = default);

    // Stages the root with its lines; committing is IUnitOfWork's job, not each repository's.
    Task AddAsync(Order order, CancellationToken cancellationToken = default);
}
