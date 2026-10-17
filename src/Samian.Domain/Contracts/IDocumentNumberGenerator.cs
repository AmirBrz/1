using Samian.Domain.Enums;
using Samian.Domain.ValueObjects;

namespace Samian.Domain.Contracts;

// The document-number sequence is a shared, gap-free counter — state that belongs to no aggregate.
// The Domain declares WHAT it needs; Infrastructure decides HOW (SQL sequence, Redis INCR, …).
//
// Returns DocumentNumber, not string: the format rule lives in the value object, so the adapter
// cannot hand back "ORD-7" and push every consumer into parsing again.
//
// Decision recorded: one counter per series, NO year dimension in the number (see DocumentNumber).
// If per-year numbering is ever needed, the year belongs to the counter key in Infrastructure and
// to DocumentNumber's format — not to this signature's callers, who should not have to know.
public interface IDocumentNumberGenerator
{
    Task<DocumentNumber> NextAsync(DocumentSeries series, CancellationToken cancellationToken = default);
}
