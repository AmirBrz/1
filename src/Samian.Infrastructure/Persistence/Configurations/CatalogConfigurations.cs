using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Samian.Domain.Common;
using Samian.Domain.Entities;
using Samian.Infrastructure.Persistence.Converters;

namespace Samian.Infrastructure.Persistence.Configurations;

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();

        builder.Property(p => p.Name).HasMaxLength(100).IsRequired();

        // The warehouse-sync key. Kept non-unique so the AddProductSku migration can be applied to an
        // already-seeded dev database (a UNIQUE index over rows defaulting to "" would collide); SKU
        // uniqueness is a data-hygiene convention enforced by the seed and the slug fallback, not here.
        builder.Property(p => p.Sku).HasMaxLength(40).IsRequired();

        builder.Property(p => p.Price)
            .HasConversion(SamianValueConverters.MoneyToString, SamianValueConverters.MoneyComparer);

        builder.Property(p => p.StockQuantity).IsRequired();

        builder.Property(p => p.TenantId).IsRequired();

        // The catalogue is browsed by name in every ERP screen, and a case-insensitive collation would
        // Every catalogue query is tenant-scoped first, so the composite index leads with TenantId;
        // this is also the index the global filter (Step 16) will seek on.
        builder.HasIndex(p => new { p.TenantId, p.Name });

        // The pipeline's SyncWarehouseAsync seeks Products by exactly this pair (job TenantId + Sku).
        builder.HasIndex(p => new { p.TenantId, p.Sku });

        builder.Ignore(nameof(AggregateRoot.DomainEvents));
    }
}

public sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("Customers");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.Name).HasMaxLength(100).IsRequired();
        builder.Property(c => c.Address).HasMaxLength(300).IsRequired();
        builder.Property(c => c.Email).HasMaxLength(200).IsRequired(false);

        builder.Property(c => c.TenantId).IsRequired();
        builder.HasIndex(c => c.TenantId);

        builder.Ignore(nameof(AggregateRoot.DomainEvents));
    }
}

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");

        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();

        // UNIQUE at the database level, not only in AuthService. The service check produces the friendly
        // 409; this index is what actually guarantees it, because two concurrent registrations can both
        // pass the check and only one can pass the index. Never rely on the application for integrity.
        //
        // The uniqueness is now PER TENANT (TenantId, UserName): the same login name in two tenants is
        // two legitimate accounts, and a user's tenant is fixed at creation (it is part of the credential).
        builder.Property(u => u.TenantId).IsRequired();
        builder.Property(u => u.UserName).HasMaxLength(20).IsRequired();
        builder.HasIndex(u => new { u.TenantId, u.UserName }).IsUnique();

        // 200 characters fits the "PBKDF2.SHA512.iterations.salt.hash" format produced by the hasher.
        builder.Property(u => u.PasswordHash).HasMaxLength(200).IsRequired();
        builder.Property(u => u.Role).HasMaxLength(30).IsRequired();
        builder.Property(u => u.IsActive).IsRequired();

        builder.Ignore(nameof(AggregateRoot.DomainEvents));
    }
}
