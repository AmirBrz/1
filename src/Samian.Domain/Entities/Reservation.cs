using Samian.Domain.Common;
using Samian.Domain.Exceptions;

namespace Samian.Domain.Entities;

// Flat, behaviourless proof of who reserved what. Not an aggregate — it is a record,
// written in the same transaction as the atomic decrement (ROADMAP step 10).
public sealed class Reservation : Entity<Guid>, IMultiTenant
{
    public Guid TenantId { get; private set; }
    public Guid SaleEventId { get; private set; }   // cross-aggregate ref: ID ONLY, per AGENTS.md
    public string Sku { get; private set; } = string.Empty;
    public int Quantity { get; private set; }
    public string CustomerRef { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }

    private Reservation() { }

    public static Reservation Record(
        Guid tenantId, Guid saleEventId, string sku, int quantity,
        string customerRef, DateTimeOffset now)
    {
        Guard.AgainstEmpty(customerRef, () => new DomainException("A reservation needs a customer reference."));

        return new Reservation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            SaleEventId = saleEventId,
            Sku = sku,
            Quantity = quantity,
            CustomerRef = customerRef.Trim(),
            CreatedAt = now
        };
    }
}