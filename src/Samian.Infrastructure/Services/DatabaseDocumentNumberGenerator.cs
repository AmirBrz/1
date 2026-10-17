using Microsoft.EntityFrameworkCore;
using Samian.Domain.Contracts;
using Samian.Domain.Enums;
using Samian.Domain.ValueObjects;
using Samian.Infrastructure.Persistence;
using Samian.Infrastructure.Persistence.Entities;

namespace Samian.Infrastructure.Services;

// IDocumentNumberGenerator, satisfied with a table instead of Redis.
//
// Why the row-per-series + compare-and-set shape: the update only lands when the counter still holds the
// value that was read, so two simultaneous requests cannot both claim ORD-000124. A loser re-reads and
// tries again, which is the same optimistic-concurrency pattern EF uses for a RowVersion column — here
// applied to a counter because a counter is the rare case where "retry the increment" is always safe.
//
// Why not Redis INCR (the obvious alternative): it is one round trip and truly atomic, and it is the
// version I would deploy with Redis already in the topology (the challenge's Redis cache is here, so
// INCR is a 10-line adapter). It is not the default because a Redis restart would reset the counter and
// hand out duplicate document numbers, and the law of a document series does not forgive that; a
// database row survives. Redis INCR + AOF persistence or a periodic DB sync would close that gap.
//
// The honest cost, which belongs in the interview answer: the counter advances even if the order is
// later rejected, so committed document numbers CAN have gaps. If the business forbids gaps, the number
// must be allocated inside the order's own transaction (one transaction + a locked row), which is a
// bigger change than it looks and is why most ERPs allow gaps and audit them instead.
//
// NOTE for whoever adds a use case: this class calls SaveChangesAsync on the SAME scoped DbContext as
// the unit of work. If it is invoked while other changes are staged, those changes commit early — which
// is why OrderService takes the number before it stages anything.
public sealed class DatabaseDocumentNumberGenerator : IDocumentNumberGenerator
{
    private const int MaxAttempts = 10;

    private readonly SamianDbContext _db;

    public DatabaseDocumentNumberGenerator(SamianDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        _db = db;
    }

    public async Task<DocumentNumber> NextAsync(DocumentSeries series, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var current = await _db.DocumentSequences
                .AsNoTracking()
                .FirstOrDefaultAsync(row => row.Series == series, cancellationToken);

            if (current is null)
            {
                try
                {
                    await _db.DocumentSequences.AddAsync(
                        new DocumentSequenceRow { Series = series, LastSequence = 1 }, cancellationToken);
                    await _db.SaveChangesAsync(cancellationToken);

                    return DocumentNumber.From(series, 1);
                }
                catch (DbUpdateException)
                {
                    // Another request created the row first (unique key on Series). That is a normal
                    // race, not a failure: drop the staged insert and re-read on the next attempt.
                    _db.ChangeTracker.Clear();
                    continue;
                }
            }

            var next = current.LastSequence + 1;

            var claimed = await _db.DocumentSequences
                .Where(row => row.Series == series && row.LastSequence == current.LastSequence)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LastSequence, next), cancellationToken);

            if (claimed == 1)
                return DocumentNumber.From(series, next);

            // 0 rows updated: somebody moved the counter between the read and the write. Retry.
        }

        throw new InvalidOperationException(
            $"Could not claim a document number for the '{series}' series after {MaxAttempts} attempts; " +
            "the counter is contended beyond what this retry loop is sized for.");
    }
}
