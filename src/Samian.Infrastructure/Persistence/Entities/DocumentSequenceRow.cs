using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Samian.Domain.Enums;

namespace Samian.Infrastructure.Persistence.Entities;

// The document-number counter, as ONE ROW PER SERIES. This is infrastructure state, not a business
// concept: the Domain asked for "the next number in the Order series" (IDocumentNumberGenerator) and
// this table is how that request is satisfied without Redis.
public sealed class DocumentSequenceRow
{
    public DocumentSeries Series { get; set; }

    public int LastSequence { get; set; }
}

public sealed class DocumentSequenceConfiguration : IEntityTypeConfiguration<DocumentSequenceRow>
{
    public void Configure(EntityTypeBuilder<DocumentSequenceRow> builder)
    {
        builder.ToTable("DocumentSequences");

        // One row per series, so the series IS the key: the alternative (a row per document, plus MAX()
        // over the table) is slower and invites two concurrent inserts to pick the same number.
        builder.HasKey(row => row.Series);

        builder.Property(row => row.Series).HasConversion<int>();
        builder.Property(row => row.LastSequence).IsRequired();
    }
}
