using Microsoft.EntityFrameworkCore;
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
    public SamianDbContext(DbContextOptions<SamianDbContext> options) : base(options)
    {
    }

    // => Set<T>() instead of DbSet<T> Properties: same thing, less typing, and no reflection surprises.
    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<User> Users => Set<User>();

    // Not a domain type, and deliberately so: a gap-free document-number counter is storage machinery.
    // Putting it in the Domain would invent an aggregate for something that is not a business concept.
    public DbSet<DocumentSequenceRow> DocumentSequences => Set<DocumentSequenceRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Every IEntityTypeConfiguration<T> in this assembly applies itself. Adding an aggregate means
        // adding a configuration CLASS, not editing this method (open/closed, applied to mapping), and it
        // keeps the fluent definitions out of a 400-line OnModelCreating.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SamianDbContext).Assembly);
    }
}
