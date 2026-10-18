using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Samian.Domain.Common;
using Samian.Domain.Entities;
using Samian.Infrastructure.Persistence.Converters;

namespace Samian.Infrastructure.Persistence.Configurations;

public sealed class SaleEventConfiguration : IEntityTypeConfiguration<SaleEvent>
{
    public void Configure(EntityTypeBuilder<SaleEvent> builder)
    {
        builder.ToTable("SaleEvents");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.TenantId).IsRequired();
        builder.Property(s => s.Sku).HasMaxLength(40).IsRequired();
        builder.Property(s => s.TotalUnits).IsRequired();
        builder.Property(s => s.RemainingUnits).IsRequired();

        // Same one-column "amount|currency" converter as every other Money on the platform.
        builder.Property(s => s.OfferPrice)
            .HasConversion(SamianValueConverters.MoneyToString, SamianValueConverters.MoneyComparer);

        // The burst read path is "which sales of THIS tenant are open NOW" — this index serves it,
        // and the global filter supplies the TenantId prefix automatically.
        builder.HasIndex(s => new { s.TenantId, s.StartsAt, s.EndsAt });

        // One active sale per SKU per tenant is ALSO a database rule now — the service check is the
        // friendly message, this is the guarantee (same split as User.UserName uniqueness).
        // NOTE: cannot encode "only one ACTIVE row" with a unique index; it guards double-CREATION of
        // the same SKU regardless of window. If overlap-by-window matters, that is a filtered unique
        // index — a deliberate simplification, listed in README limitations.
        builder.HasIndex(s => new { s.TenantId, s.Sku }).IsUnique();

        builder.Ignore(nameof(AggregateRoot.DomainEvents));
    }
}

public sealed class ReservationConfiguration : IEntityTypeConfiguration<Reservation>
{
    public void Configure(EntityTypeBuilder<Reservation> builder)
    {
        builder.ToTable("Reservations");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.SaleEventId).IsRequired();
        builder.Property(r => r.Sku).HasMaxLength(40).IsRequired();
        builder.Property(r => r.Quantity).IsRequired();
        builder.Property(r => r.CustomerRef).HasMaxLength(100).IsRequired();

        // "Show me this sale's reservations" is the only query it serves. No FK navigations —
        // SaleEvent is another aggregate (ID-ONLY rule).
        builder.HasIndex(r => new { r.TenantId, r.SaleEventId });
    }
}