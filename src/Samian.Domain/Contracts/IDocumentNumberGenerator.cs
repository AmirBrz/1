using Samian.Domain.Enums;
using Samian.Domain.ValueObjects;

namespace Samian.Domain.Contracts;

// The document-number sequence is a shared, gap-free counter — state that belongs to no aggregate.
// The Domain declares WHAT it needs; Infrastructure decides HOW (SQL sequence, Redis INCR, …).
//
// Returns DocumentNumber, not string: the format rule lives in the value object, so the adapter
// cannot hand back "ORD-7" and push every consumer into parsing again.
//
// OPEN QUESTION (your previous signature had a `year` parameter): if numbers must be per-year
// series like ORD-1405-000123, DocumentNumber gains a Year component and the pattern becomes
// ^[A-Z]{3}-\d{4}-\d{6}$ — one format change, plus a year argument here.
public interface IDocumentNumberGenerator
{
    Task<DocumentNumber> NextAsync(DocumentSeries series, CancellationToken cancellationToken = default);
}
