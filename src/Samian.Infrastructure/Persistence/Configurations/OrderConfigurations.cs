using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Samian.Domain.Common;
using Samian.Domain.Entities;
using Samian.Infrastructure.Persistence.Converters;

namespace Samian.Infrastructure.Persistence.Configurations;

public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders");

        builder.HasKey(o => o.Id);

        // Client-assigned identity (Order.Create does Guid.NewGuid()): SQLite cannot generate a Guid key
        // the way SQL Server does, and leaving the default would throw on the first insert.
        builder.Property(o => o.Id).ValueGeneratedNever();

        builder.Property(o => o.CustomerId).IsRequired();

        builder.Property(o => o.Currency).HasMaxLength(3).IsRequired();

        // Converter + comparer: the comparer is what lets Where(o => o.Number == someNumber) compare the
        // provider string instead of failing to translate a value-object equality.
        // CS8620 suppressed, not ignored: the property is DocumentNumber? while the converter is
        // ValueConverter<DocumentNumber, string>. Nullable REFERENCE annotations erase at runtime, so
        // the two generic instantiations ARE the same type — and EF's ConvertToProvider wrapper applies
        // its own null check before the lambda runs, which is exactly what unnumbered drafts rely on.
#pragma warning disable CS8620
        builder.Property(o => o.Number)
            .HasConversion(SamianValueConverters.DocumentNumberToString, SamianValueConverters.DocumentNumberComparer)
            .HasMaxLength(DocumentNumberLength)
            .IsRequired(false);
#pragma warning restore CS8620

        // NULLS are allowed: several drafts may exist before any of them has a number. The number itself
        // is unique once assigned, which is the constraint an ERP document actually needs.
        builder.HasIndex(o => o.Number).IsUnique();

        // Enum stored as its declared NUMBER (OrderStatus.Draft = 1): casting to int here keeps the
        // database values aligned with the enum's explicit numbering instead of alphabetical order.
        builder.Property(o => o.Status).HasConversion<int>().IsRequired();

        builder.Property(o => o.Total)
            .HasConversion(SamianValueConverters.MoneyToString, SamianValueConverters.MoneyComparer);

        builder.Property(o => o.Discount)
            .HasConversion(SamianValueConverters.MoneyToString, SamianValueConverters.MoneyComparer);

        // THE POINT OF THE AGGREGATE'S ENCAPSULATION MEETS EF: a read-only collection property with a
        // private backing list is exactly what EF's "field-only persistence" mode was designed for.
        // Without Field access mode EF would look for a settable collection and, failing that, would
        // REPLACE the list on materialization — which is how the old public-setter version lost lines.
        builder.Navigation(o => o.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Owns-one (child) of the root: no independent repository, cascade delete, and the FK lives on
        // OrderLine. `WithOne()` with no argument because the child deliberately has NO navigation back
        // to the root: an OrderLine that can walk to its Order is a door out of the boundary.
        builder.HasMany(o => o.Lines)
            .WithOne()
            .HasForeignKey(l => l.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        // Computed from the lines: a column for it would be a second source of truth that can disagree
        // with Total. (This is the line to show in an interview: EF maps state, not arithmetic.)
        builder.Ignore(o => o.Subtotal);

        // Infrastructure plumbing on the base class: the event buffer must never become a table.
        builder.Ignore(nameof(AggregateRoot.DomainEvents));

        builder.Property(o => o.CancellationReason).HasMaxLength(500).IsRequired(false);
    }

    // "ORD-000123": DocumentNumber.SequenceDigits + prefix + separator, kept in sync by hand because
    // EF's HasMaxLength needs a constant expression at configuration time.
    private const int DocumentNumberLength = 10;
}

public sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> builder)
    {
        builder.ToTable("OrderLines");

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();

        // The snapshot price is the reason this child entity exists; storing it is not duplication
        // because it is HISTORY, independent of whatever Product.Price becomes tomorrow.
        builder.Property(l => l.UnitPrice)
            .HasConversion(SamianValueConverters.MoneyToString, SamianValueConverters.MoneyComparer);

        builder.Property(l => l.ProductId).IsRequired();
        builder.Property(l => l.Quantity).IsRequired();

        builder.Ignore(l => l.LineTotal);
        builder.Ignore(nameof(AggregateRoot.DomainEvents));

        // Every "load the order then touch its lines" query hits this index; without it SQLite scans
        // the whole line table per order.
        builder.HasIndex(l => l.OrderId);
    }
}
