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
}
