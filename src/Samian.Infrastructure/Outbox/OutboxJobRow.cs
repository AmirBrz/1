using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Samian.Infrastructure.Outbox;

// The transactional outbox: ONE table that turns "the job was accepted" into durable rows that
// commit in the SAME transaction as the reservation decrement (brief line 21: a failed pipeline
// must not make the task disappear without a trace). Infrastructure-only, like DocumentSequenceRow
// and TenantRow — storage machinery, not a business aggregate; the Domain does not know queues exist.
//
// Status is a human-readable STRING, not an enum number: `SELECT * FROM OutboxJobs WHERE
// Status='Dead'` in SSMS is part of the reviewer-facing evidence, and 'Dead' is the dead-letter
// queue — no separate DLQ table to explain.
public sealed class OutboxJobRow
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }
    public Guid SaleEventId { get; set; }
    public Guid ReservationId { get; set; }

    // The frozen payload (what to fulfil), not a live reference: the worker must never re-read
    // volatile state to do its job.
    public string Sku { get; set; } = null!;
    public int Quantity { get; set; }
    public decimal UnitPriceAmount { get; set; }
    public string Currency { get; set; } = null!;
    public string CustomerRef { get; set; } = null!;

    public string Status { get; set; } = OutboxStatus.Pending;
    public int Attempts { get; set; }

    // When the row becomes claimable: now for fresh jobs, later for retries (backoff lives HERE).
    public DateTimeOffset NextAt { get; set; }

    // Set at claim time so a crashed worker's row is reclaimed after the lease expires —
    // "kill the worker mid-job, the job returns to Pending" from the acceptance checklist.
    public DateTimeOffset? ClaimedAt { get; set; }

    public string? Error { get; set; }
}

public static class OutboxStatus
{
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string Done = "Done";
    public const string Dead = "Dead";      // attempts exhausted = the dead-letter queue
}

public sealed class OutboxJobConfiguration : IEntityTypeConfiguration<OutboxJobRow>
{
    public void Configure(EntityTypeBuilder<OutboxJobRow> builder)
    {
        builder.ToTable("OutboxJobs");

        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).ValueGeneratedNever();

        builder.Property(j => j.Status).HasMaxLength(20).IsRequired();
        builder.Property(j => j.Sku).HasMaxLength(40).IsRequired();
        builder.Property(j => j.CustomerRef).HasMaxLength(100).IsRequired();
        builder.Property(j => j.Currency).HasMaxLength(3).IsRequired();
        builder.Property(j => j.UnitPriceAmount).HasPrecision(18, 2);
        builder.Property(j => j.Error).HasMaxLength(4000).IsRequired(false);

        // THE polling index: "give me claimable jobs, oldest first" must be a seek, not a scan —
        // under burst traffic this query runs every second per worker instance.
        builder.HasIndex(j => new { j.Status, j.NextAt });
        builder.HasIndex(j => new { j.TenantId, j.Status });
    }
}
