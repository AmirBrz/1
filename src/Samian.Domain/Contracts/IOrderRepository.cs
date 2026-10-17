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
    // returns the complete aggregate lines included.
    Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Order?> GetByNumberAsync(DocumentNumber number, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Order>> GetForCustomerAsync(Guid customerId, CancellationToken cancellationToken = default);

    // Paging parameters belong on the port: `GetPagedAsync()` with no arguments cannot page anything.
    // (A richer PagedRequest/PagedResult contract belongs in Application, once the queries exist.)
    Task<IReadOnlyList<Order>> GetPagedAsync(int page, int pageSize, CancellationToken cancellationToken = default);

    // Stages the root with its lines; committing is IUnitOfWork's job, not each repository's.
    Task AddAsync(Order order, CancellationToken cancellationToken = default);
}
