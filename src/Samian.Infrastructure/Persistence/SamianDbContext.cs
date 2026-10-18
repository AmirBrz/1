using Microsoft.EntityFrameworkCore;
using Samian.Application.Contracts;
using Samian.Domain.Entities;
using Samian.Infrastructure.Persistence.Entities;

namespace Samian.Infrastructure.Persistence;

// The only class in the solution that knows about tables. Nothing in Domain or Application can reach it:
// repositories hand out aggregates, and IUnitOfWork is the only door to a commit.
//
// DbContext lifetime: SCOPED (one instance per HTTP request), which is what makes the unit of work
// genuinely one transaction per use case. Registering it as a singleton is the classic EF bug — two
// requests then share a change tracker and overwrite each other.
public sealed class SamianDbContext : DbContext
{

    private readonly Guid? _tenantId;

    // `?` on the parameter: the design-time factory and every non-request scope legitimately have no
    // tenant source — that is the unfiltered path, not an accident.
    public SamianDbContext(DbContextOptions<SamianDbContext> options, ITenantProvider? tenant = null) : base(options)
    {
        _tenantId = tenant is { HasTenant: true } && tenant.TenantId != Guid.Empty ? tenant.TenantId : null;
    }

    // => Set<T>() instead of DbSet<T> Properties: same thing, less typing, and no reflection surprises.
    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<User> Users => Set<User>();

    public DbSet<SaleEvent> SaleEvents => Set<SaleEvent>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<DocumentSequenceRow> DocumentSequences => Set<DocumentSequenceRow>();

    // The registry the tenant middleware validates against. Deliberately NOT filtered and NOT
    // IMultiTenant — it is the thing tenants are named by, not a thing a tenant owns.
    public DbSet<TenantRow> Tenants => Set<TenantRow>();

    // The transactional outbox (step 17): also storage machinery, also unfiltered — the pipeline
    // worker processes EVERY tenant's jobs and each query carries its tenant id explicitly.
    public DbSet<Outbox.OutboxJobRow> OutboxJobs => Set<Outbox.OutboxJobRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Every IEntityTypeConfiguration<T> in this assembly applies itself. Adding an aggregate means
        // adding a configuration CLASS, not editing this method (open/closed, applied to mapping), and it
        // keeps the fluent definitions out of a 400-line OnModelCreating.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SamianDbContext).Assembly);
   
        // one global filter per tenant-bearing entity. `_tenantId == null` short-circuit
        // exists ONLY so design-time model building works; at runtime it is always a real id, and
        // when it is, every query (including ExecuteUpdateAsync's WHERE) gains AND TenantId = @p.
        modelBuilder.Entity<Order>().HasQueryFilter(e => _tenantId == null || e.TenantId == _tenantId);
        modelBuilder.Entity<Product>().HasQueryFilter(e => _tenantId == null || e.TenantId == _tenantId);
        modelBuilder.Entity<Customer>().HasQueryFilter(e => _tenantId == null || e.TenantId == _tenantId);
        modelBuilder.Entity<SaleEvent>().HasQueryFilter(e => _tenantId == null || e.TenantId == _tenantId);
        modelBuilder.Entity<Reservation>().HasQueryFilter(e => _tenantId == null || e.TenantId == _tenantId);
   
    }
}
