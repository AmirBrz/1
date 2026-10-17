using Microsoft.EntityFrameworkCore;
using Samian.Domain.Common;
using Samian.Domain.Contracts;
using Samian.Domain.Events;
using Samian.Application.Contracts;

namespace Samian.Infrastructure.Persistence;

// The IUnitOfWork the Domain declared. Two responsibilities, and nothing else:
//   1) commit the staged aggregates (one SaveChanges = one database transaction on EF Core), and
//   2) hand the raised facts to the dispatcher AFTER the commit succeeded.
//
// The order is the whole design. Dispatching before SaveChanges would announce an order that a
// constraint could still reject; dispatching after means a handler can rely on the fact being real.
// Clearing the buffer after dispatch is what stops the same OrderPlaced firing twice when the same
// aggregate instance is touched again inside one request scope.
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly SamianDbContext _db;
    private readonly IDomainEventDispatcher _dispatcher;

    public UnitOfWork(SamianDbContext db, IDomainEventDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _db = db;
        _dispatcher = dispatcher;
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        // Snapshot the events BEFORE saving: SaveChanges can (legitimately) leave the entities tracked,
        // and a failure path must not have dispatched anything — so the events are only read once the
        // write is proven. ChangeTracker sees every aggregate touched during the request, which is why
        // the repositories stage instead of committing.
        var aggregatesWithEvents = _db.ChangeTracker
            .Entries<AggregateRoot>()
            .Select(entry => entry.Entity)
            .Where(aggregate => aggregate.DomainEvents.Count > 0)
            .ToList();

        var pendingEvents = aggregatesWithEvents
            .SelectMany(aggregate => aggregate.DomainEvents)
            .ToList();

        var written = await _db.SaveChangesAsync(cancellationToken);

        if (pendingEvents.Count == 0)
            return written;

        await _dispatcher.DispatchAsync(pendingEvents, cancellationToken);

        foreach (var aggregate in aggregatesWithEvents)
            aggregate.ClearDomainEvents();

        return written;
    }
}
