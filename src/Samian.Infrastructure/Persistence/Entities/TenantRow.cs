using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Samian.Infrastructure.Persistence.Entities;

// The tenant REGISTRY: two columns that answer "does this slug/id name a real tenant?".
// Infrastructure-only, like DocumentSequenceRow: the Domain's multi-tenancy contract is the
// IMultiTenant marker, not an aggregate. A Tenant aggregate with behaviour would leak
// cross-aggregate navigation into every entity for zero business rules.
public sealed class TenantRow
{
    public Guid Id { get; set; }

    // "tenant-electronics" — the human half of X-Tenant-Id; the brief's own naming convention.
    public string Slug { get; set; } = null!;
}

public sealed class TenantRowConfiguration : IEntityTypeConfiguration<TenantRow>
{
    public void Configure(EntityTypeBuilder<TenantRow> builder)
    {
        builder.ToTable("Tenants");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.Slug).HasMaxLength(50).IsRequired();

        // UNIQUE in the database, same doctrine as User.UserName: the service check gives the
        // friendly message, the index gives the guarantee.
        builder.HasIndex(t => t.Slug).IsUnique();
    }
}
