using Microsoft.EntityFrameworkCore;
using Samian.Domain.Contracts;
using Samian.Domain.Entities;

namespace Samian.Infrastructure.Persistence.Repositories;

public sealed class ProductRepository : IProductRepository
{
    private readonly SamianDbContext _db;

    public ProductRepository(SamianDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        _db = db;
    }

    public Task<Product?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _db.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Product>> GetByIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        // Translates to WHERE Id IN (...): ONE round trip for the whole order. The alternative
        // (a foreach calling GetByIdAsync) is the N+1 query the port's comment warns about.
        var list = await _db.Products
            .Where(p => ids.Contains(p.Id))
            .ToListAsync(cancellationToken);

        return list;
    }

    public async Task<IReadOnlyList<Product>> GetPagedAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        // Ordered by Name rather than Id: Id is a random Guid, so paging by it produces a page order
        // that no human can reproduce, while Name is what the catalogue screen sorts by anyway.
        return await _db.Products
            .OrderBy(p => p.Name)
            .ThenBy(p => p.Id)
            .Skip(page * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Product product, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);

        await _db.Products.AddAsync(product, cancellationToken);
    }

    public Task UpdateAsync(Product product, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);

        // An aggregate loaded through this repository is already tracked, so marking it Modified again
        // would be a no-op at best; the guard is for the detached path (a DTO mapped back into an entity).
        var entry = _db.Entry(product);

        if (entry.State == EntityState.Detached)
            entry.State = EntityState.Modified;

        return Task.CompletedTask;
    }

    public Task DeleteAsync(Product product, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);

        _db.Products.Remove(product);

        return Task.CompletedTask;
    }
}

public sealed class CustomerRepository : ICustomerRepository
{
    private readonly SamianDbContext _db;

    public CustomerRepository(SamianDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        _db = db;
    }

    public Task<Customer?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _db.Customers.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task AddAsync(Customer customer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customer);

        await _db.Customers.AddAsync(customer, cancellationToken);
    }
}

public sealed class UserRepository : IUserRepository
{
    private readonly SamianDbContext _db;

    public UserRepository(SamianDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        _db = db;
    }

    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public Task<User?> GetByUserNameAsync(string userName, CancellationToken cancellationToken = default)
        => _db.Users.FirstOrDefaultAsync(u => u.UserName == userName, cancellationToken);

    public async Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        await _db.Users.AddAsync(user, cancellationToken);
    }
}
