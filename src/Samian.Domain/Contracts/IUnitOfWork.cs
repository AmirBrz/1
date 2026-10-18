namespace Samian.Domain.Contracts;

// One transaction boundary per use case. Repositories stage changes; this is the single place that
// commits, so "place an order + deduct stock + save" either fully happened or fully didn't.
//
// What is deliberately NOT here: no DbContext, no IQueryable, no per-repository SaveChanges. The
// domain states the need; Infrastructure satisfies it. This is also where domain events get
// dispatched (after the write succeeds, before the response goes out).
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);


// The reservation use case needs statements BEYOND tracked entities to share one commit:
    // the atomic ExecuteUpdate (SaleEventRepository) runs on the connection directly, and the
    // outbox row (Step 17) must land in the SAME transaction — "a job must never disappear"
    // (brief line 21) is only true if decrement+reservation+job commit together or not at all.
    // Two methods, no IDbContextTransaction leaking into Application: the port stays a port.
    Task BeginTransactionAsync(CancellationToken cancellationToken = default);
    Task CommitTransactionAsync(CancellationToken cancellationToken = default);}
